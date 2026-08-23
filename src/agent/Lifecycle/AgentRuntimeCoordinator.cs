using System.Diagnostics;
using System.Net.WebSockets;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Configuration;
using CodexControl.Agent.Control;
using CodexControl.Agent.Diagnostics;
using CodexControl.Agent.Proxy;
using CodexControl.Agent.Relay;
using CodexControl.Agent.Security;
using CodexControl.Agent.State;
using CodexControl.Protocol;

namespace CodexControl.Agent.Lifecycle;

public sealed class AgentRuntimeCoordinator : IAsyncDisposable
{
    private static readonly int[] RestartSeconds = [1, 2, 5, 10, 30];

    private readonly AgentDataPaths _paths;
    private readonly AgentStateStore _stateStore;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _restartSignal = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _snapshotGate = new();

    private AgentOptions _options;
    private AgentLog? _log;
    private Task? _runTask;
    private CodexStateManager? _state;
    private AppServerBridge? _bridge;
    private LocalCodexProxyServer? _proxy;
    private RelayClient? _relay;
    private DeviceIdentity? _identity;
    private AgentRuntimeSnapshot _snapshot = AgentRuntimeSnapshot.Initial;
    private bool _remoteAccessPaused;
    private bool _restartPending;
    private bool _reloadIdentityOnRestart;
    private int _disposed;

    public AgentRuntimeCoordinator(AgentOptions options, AgentDataPaths paths, bool remoteAccessPaused)
    {
        _options = options;
        _paths = paths;
        _stateStore = new AgentStateStore(paths);
        _remoteAccessPaused = remoteAccessPaused;
    }

    public AgentRuntimeSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public Task Completion => _runTask ?? Task.CompletedTask;

    public event Action<AgentRuntimeSnapshot>? SnapshotChanged;
    public event Action<PairingConfirmationRequestedPayload>? PairingConfirmationRequested;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_runTask is not null)
        {
            throw new InvalidOperationException("Runtime Coordinator 只能启动一次。");
        }

        _paths.EnsureWritable();
        _log = new AgentLog(_paths.LogDirectory);
        _log.Info("startup", ".NET 8 CodexControlAgent runtime starting");
        _runTask = RunAsync(_lifetime.Token);
    }

    public async Task<PairingCreatedPayload> CreatePairingAsync(CancellationToken cancellationToken)
    {
        RelayClient relay;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            relay = _relay ?? throw new AgentException("RELAY_OFFLINE", "Relay 尚未连接。");
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await relay.WaitUntilAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
        return await relay.CreatePairingAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResolvePairingAsync(
        PairingConfirmationResolvePayload payload,
        CancellationToken cancellationToken)
    {
        RelayClient relay;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            relay = _relay ?? throw new AgentException("RELAY_OFFLINE", "Relay 尚未连接。");
        }
        finally
        {
            _lifecycleGate.Release();
        }

        _ = await relay.ResolvePairingAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PairedControllerSummaryPayload>> ListPairingsAsync(
        CancellationToken cancellationToken)
    {
        var relay = await GetRelayAsync(cancellationToken).ConfigureAwait(false);
        return (await relay.ListPairingsAsync(cancellationToken).ConfigureAwait(false)).Controllers;
    }

    public async Task<PairingUpdatedPayload> UpdatePairingAsync(
        PairingUpdatePayload payload,
        CancellationToken cancellationToken)
    {
        var relay = await GetRelayAsync(cancellationToken).ConfigureAwait(false);
        return await relay.UpdatePairingAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RevokePairingAsync(long pairingId, CancellationToken cancellationToken)
    {
        try
        {
            var relay = await GetRelayAsync(cancellationToken).ConfigureAwait(false);
            await relay.RevokePairingAsync(pairingId, cancellationToken).ConfigureAwait(false);
            _stateStore.RemoveRevocation(pairingId);
            return true;
        }
        catch (Exception exception) when (
            exception is TimeoutException or WebSocketException ||
            exception is AgentException { Code: "RELAY_OFFLINE" })
        {
            _stateStore.AddRevocation(pairingId);
            return false;
        }
    }

    public async Task PauseRemoteAccessAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _remoteAccessPaused = true;
            await StopRelayLockedAsync().ConfigureAwait(false);
            Publish(snapshot => snapshot with
            {
                RelayStatus = RuntimeRelayStatus.Paused,
                Summary = snapshot.CoreStatus == RuntimeCoreStatus.Ready
                    ? "本机可用，远程访问已暂停"
                    : snapshot.Summary,
            });
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task ResumeRemoteAccessAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _remoteAccessPaused = false;
            await StartRelayLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public bool RequestRestart(bool force)
    {
        var activeTurn = Snapshot.Codex.ActiveTurnId is not null;
        _restartPending = true;
        Publish(snapshot => snapshot with
        {
            CoreStatus = RuntimeCoreStatus.RestartPending,
            RestartPending = true,
            Summary = activeTurn && !force ? "当前任务完成后重启" : "正在重启 Codex 内核",
        });
        if (!activeTurn || force)
        {
            SignalRestart();
            return true;
        }

        return false;
    }

    public void UpdateOptions(AgentOptions options, bool remoteAccessPaused)
    {
        _options = options;
        _remoteAccessPaused = remoteAccessPaused;
        _reloadIdentityOnRestart = true;
    }

    public void OpenLocalTerminal()
    {
        var snapshot = Snapshot;
        if (snapshot.CoreStatus != RuntimeCoreStatus.Ready || snapshot.LocalProxyUri is null)
        {
            throw new AgentException(AgentErrorCodes.AppServerDisconnected, "Codex 内核尚未就绪。");
        }

        if (snapshot.LocalTuiConnected)
        {
            throw new AgentException("TUI_ALREADY_CONNECTED", "本机 Codex 终端已经连接。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.CodexPath,
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add("--remote");
        startInfo.ArgumentList.Add(snapshot.LocalProxyUri.ToString());
        _ = Process.Start(startInfo) ?? throw new AgentException(
            AgentErrorCodes.CodexNotExecutable,
            "无法打开 Codex 终端。");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifetime.IsCancellationRequested)
        {
            if (_runTask is not null)
            {
                await _runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        _lifetime.Cancel();
        SignalRestart();
        if (_runTask is not null)
        {
            await _runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _identity?.Dispose();
            _log?.Dispose();
            _lifetime.Dispose();
            _lifecycleGate.Dispose();
            _restartSignal.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            Publish(snapshot => snapshot with
            {
                CoreStatus = RuntimeCoreStatus.Discovering,
                RelayStatus = _remoteAccessPaused
                    ? RuntimeRelayStatus.Paused
                    : _options.RelayUrl is null
                        ? RuntimeRelayStatus.Disabled
                        : RuntimeRelayStatus.Offline,
                Summary = "正在查找 Codex",
                LastError = null,
            });
            var runtime = await CodexRuntimeResolver.ResolveAsync(
                _options.CodexPath,
                _paths.DataDirectory,
                cancellationToken).ConfigureAwait(false);
            _options = _options with { CodexPath = runtime.ExecutablePath };
            _log!.Info(
                "codex_runtime_resolved",
                $"Codex runtime source={runtime.Source}; staged={runtime.WasStaged}; " +
                $"package={runtime.DesktopPackageName ?? "none"}");

            Publish(snapshot => snapshot with
            {
                CoreStatus = RuntimeCoreStatus.Probing,
                Summary = "正在检查 Codex 能力",
                CodexPath = runtime.ExecutablePath,
            });
            var probe = await CodexExecutableProbe.ProbeAsync(
                runtime.ExecutablePath,
                cancellationToken).ConfigureAwait(false);
            _log.Info("codex_probe_succeeded", $"Codex capability probe succeeded; version={probe.Version}");
            Publish(snapshot => snapshot with
            {
                CodexPath = runtime.ExecutablePath,
                CodexVersion = probe.Version,
            });

            var restartAttempt = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var startedAt = DateTimeOffset.UtcNow;
                await StartCoreAsync(cancellationToken).ConfigureAwait(false);
                var bridge = _bridge ?? throw new InvalidOperationException("Bridge was not created.");
                using var restartWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var restartTask = _restartSignal.WaitAsync(restartWait.Token);
                var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                var completed = await Task.WhenAny(bridge.Completion, restartTask, cancellationTask)
                    .ConfigureAwait(false);
                restartWait.Cancel();

                if (ReferenceEquals(completed, bridge.Completion))
                {
                    var exitCode = await bridge.Completion.ConfigureAwait(false);
                    _log.Error("app_server_session_failed", $"app-server exited unexpectedly; code={exitCode}");
                }

                await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var requestedRestart = _restartPending || ReferenceEquals(completed, restartTask);
                _restartPending = false;
                if (requestedRestart)
                {
                    restartAttempt = 0;
                    continue;
                }

                if (DateTimeOffset.UtcNow - startedAt > TimeSpan.FromMinutes(1))
                {
                    restartAttempt = 0;
                }

                var delay = RestartSeconds[Math.Min(restartAttempt++, RestartSeconds.Length - 1)];
                Publish(snapshot => snapshot with
                {
                    CoreStatus = RuntimeCoreStatus.Failed,
                    Summary = $"Codex 内核异常，{delay} 秒后重试",
                });
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log?.Error("agent_runtime_failed", "Agent runtime failed", exception);
            Publish(snapshot => snapshot with
            {
                CoreStatus = RuntimeCoreStatus.Failed,
                RelayStatus = _remoteAccessPaused ? RuntimeRelayStatus.Paused : RuntimeRelayStatus.Offline,
                Summary = "Codex 不可用，需要处理",
                LastError = exception is AgentException agentException
                    ? $"{agentException.Code}: {agentException.Message}"
                    : exception.Message,
            });
        }
        finally
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            Publish(snapshot => snapshot with
            {
                CoreStatus = RuntimeCoreStatus.Stopped,
                RelayStatus = _remoteAccessPaused ? RuntimeRelayStatus.Paused : RuntimeRelayStatus.Offline,
                Summary = "已停止",
                LocalProxyUri = null,
                LocalTuiConnected = false,
            });
            _log?.Info("shutdown", "CodexControlAgent runtime stopped");
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Publish(snapshot => snapshot with
            {
                CoreStatus = RuntimeCoreStatus.Starting,
                Summary = "正在启动 Codex 内核",
                RestartPending = false,
                LastError = null,
            });
            var state = new CodexStateManager();
            state.SnapshotChanged += OnCodexSnapshotChanged;
            var bridge = new AppServerBridge(_options, _log!, state);
            var proxy = new LocalCodexProxyServer(_options, bridge, _log!);
            proxy.ClientConnectionChanged += OnTuiConnectionChanged;
            _state = state;
            _bridge = bridge;
            _proxy = proxy;
            if (_options.RelayUrl is not null && _identity is null)
            {
                _identity = DeviceIdentity.LoadOrCreate(_paths.DataDirectory, _options.DeviceName);
            }
            await bridge.StartAsync(cancellationToken).ConfigureAwait(false);
            await proxy.StartAsync(cancellationToken).ConfigureAwait(false);
            await StartRelayLockedAsync().ConfigureAwait(false);
            Publish(snapshot => snapshot with
            {
                CoreStatus = RuntimeCoreStatus.Ready,
                Summary = ResolveSummary(RuntimeCoreStatus.Ready, snapshot.RelayStatus),
                LocalProxyUri = proxy.WebSocketUri,
                RestartPending = false,
                Codex = state.Snapshot,
            });
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_bridge is null && _proxy is null && _relay is null)
            {
                return;
            }

            Publish(snapshot => snapshot with
            {
                CoreStatus = RuntimeCoreStatus.Stopping,
                Summary = "正在停止 Codex 内核",
            });
            await StopRelayLockedAsync().ConfigureAwait(false);
            if (_proxy is not null)
            {
                _proxy.ClientConnectionChanged -= OnTuiConnectionChanged;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _proxy.StopAsync(timeout.Token).ConfigureAwait(false);
                await _proxy.DisposeAsync().ConfigureAwait(false);
                _proxy = null;
            }

            if (_bridge is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _bridge.StopAsync(timeout.Token).ConfigureAwait(false);
                await _bridge.DisposeAsync().ConfigureAwait(false);
                _bridge = null;
            }

            if (_state is not null)
            {
                _state.SnapshotChanged -= OnCodexSnapshotChanged;
                _state = null;
            }

            if (_reloadIdentityOnRestart)
            {
                _identity?.Dispose();
                _identity = null;
                _reloadIdentityOnRestart = false;
            }
        }
        catch (Exception exception)
        {
            _log?.Error("runtime_stop_failed", "Runtime shutdown failed", exception);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private Task StartRelayLockedAsync()
    {
        if (_remoteAccessPaused)
        {
            Publish(snapshot => snapshot with
            {
                RelayStatus = RuntimeRelayStatus.Paused,
                Summary = ResolveSummary(snapshot.CoreStatus, RuntimeRelayStatus.Paused),
            });
            return Task.CompletedTask;
        }

        if (_options.RelayUrl is null || _bridge is null || _state is null || _identity is null)
        {
            Publish(snapshot => snapshot with
            {
                RelayStatus = RuntimeRelayStatus.Disabled,
                Summary = ResolveSummary(snapshot.CoreStatus, RuntimeRelayStatus.Disabled),
            });
            return Task.CompletedTask;
        }

        if (_relay is not null)
        {
            return Task.CompletedTask;
        }

        var dispatcher = new RemoteControlDispatcher(_bridge, _state);
        var pendingRevocations = _stateStore.Load().PendingPairingRevocations;
        _relay = new RelayClient(
            _options,
            _identity,
            _state,
            _bridge,
            dispatcher,
            _log!,
            pendingRevocations,
            pairingId => _stateStore.RemoveRevocation(pairingId));
        _relay.PairingConfirmationRequested += OnPairingConfirmationRequested;
        _relay.Start();
        Publish(snapshot => snapshot with
        {
            RelayStatus = RuntimeRelayStatus.Connecting,
            Summary = ResolveSummary(snapshot.CoreStatus, RuntimeRelayStatus.Connecting),
        });
        return Task.CompletedTask;
    }

    private async Task StopRelayLockedAsync()
    {
        if (_relay is null)
        {
            return;
        }

        var relay = _relay;
        _relay = null;
        relay.PairingConfirmationRequested -= OnPairingConfirmationRequested;
        await relay.DisposeAsync().ConfigureAwait(false);
        _state?.SetRelayConnected(false);
    }

    private void OnCodexSnapshotChanged(CodexStateSnapshot codex)
    {
        var relayStatus = _remoteAccessPaused
            ? RuntimeRelayStatus.Paused
            : codex.RelayConnected
                ? RuntimeRelayStatus.Online
                : _options.RelayUrl is null
                    ? RuntimeRelayStatus.Disabled
                    : RuntimeRelayStatus.Offline;
        Publish(snapshot => snapshot with
        {
            RelayStatus = relayStatus,
            Summary = ResolveSummary(snapshot.CoreStatus, relayStatus),
            Codex = codex,
            LastError = codex.LastError ?? snapshot.LastError,
        });
        if (_restartPending && codex.ActiveTurnId is null)
        {
            SignalRestart();
        }
    }

    private void OnTuiConnectionChanged(bool connected) => Publish(snapshot => snapshot with
    {
        LocalTuiConnected = connected,
    });

    private void OnPairingConfirmationRequested(PairingConfirmationRequestedPayload requested)
    {
        var handlers = PairingConfirmationRequested;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<PairingConfirmationRequestedPayload> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(requested);
            }
            catch
            {
            }
        }
    }

    private void Publish(Func<AgentRuntimeSnapshot, AgentRuntimeSnapshot> transform)
    {
        AgentRuntimeSnapshot published;
        lock (_snapshotGate)
        {
            published = transform(_snapshot) with { Revision = _snapshot.Revision + 1 };
            Volatile.Write(ref _snapshot, published);
        }

        var handlers = SnapshotChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<AgentRuntimeSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(published);
            }
            catch
            {
            }
        }
    }

    private void SignalRestart()
    {
        try
        {
            _restartSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task<RelayClient> GetRelayAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _relay ?? throw new AgentException("RELAY_OFFLINE", "Relay 尚未连接。");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static string ResolveSummary(RuntimeCoreStatus core, RuntimeRelayStatus relay) => core switch
    {
        RuntimeCoreStatus.Ready when relay == RuntimeRelayStatus.Online => "可以使用",
        RuntimeCoreStatus.Ready when relay == RuntimeRelayStatus.Paused => "本机可用，远程访问已暂停",
        RuntimeCoreStatus.Ready when relay is RuntimeRelayStatus.Offline or RuntimeRelayStatus.Connecting =>
            "本机可用，远程离线",
        RuntimeCoreStatus.Ready => "本机可以使用",
        RuntimeCoreStatus.Failed => "Codex 不可用，需要处理",
        RuntimeCoreStatus.Stopped => "已停止",
        _ => "正在启动",
    };
}
