using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexControl.Agent.Configuration;
using CodexControl.Agent.Diagnostics;
using CodexControl.Agent.State;

namespace CodexControl.Agent.Codex;

/// <summary>
/// Owns an independent stdio process, or only a client connection to a verified shared WS service.
/// </summary>
public sealed class AppServerBridge : IAsyncDisposable
{
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WriterShutdownTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProcessShutdownTimeout = TimeSpan.FromSeconds(5);
    private const int MinimumAppServerMessageBytes = 128 * 1024 * 1024;

    private readonly AgentOptions _options;
    private readonly AgentLog _log;
    private readonly CodexStateManager _state;
    private readonly Channel<string> _outbound;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RpcReply>> _pendingInternal = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly object _clientGate = new();
    private readonly TaskCompletionSource<int> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Process? _process;
    private ClientWebSocket? _sharedSocket;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private Task? _writerTask;
    private Task? _monitorTask;
    private IAppServerClientSink? _client;
    private JsonElement _initializeResult;
    private int _started;
    private int _stopping;
    private int _disposed;
    private int _ready;

    public AppServerBridge(AgentOptions options, AgentLog log, CodexStateManager state)
    {
        _options = options;
        _log = log;
        _state = state;
        AuthorizedProjectRoots = SharedProjectAuthorization.Validate(options.SharedProjectRoots);
        _state.AwaitServerResolution = IsSharedSession;
        Approvals = new ApprovalCoordinator(QueueOutboundAsync, log, awaitServerResolution: IsSharedSession);
        _outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    public bool IsReady => Volatile.Read(ref _ready) != 0;
    public bool IsSharedSession => _options.SharedEndpoint is not null;
    public string ServiceInstanceId { get; private set; } = string.Empty;
    public string ConnectionEpoch { get; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<string> AuthorizedProjectRoots { get; }
    public bool AllowStandardTemporaryDirectories => _options.SharedAllowStandardTemporaryDirectories;

    public ApprovalCoordinator Approvals { get; }

    public event Action<JsonElement>? ServerMessageReceived;

    public JsonElement InitializeResult => IsReady
        ? _initializeResult
        : throw new InvalidOperationException("App-server bridge 尚未完成初始化。");

    public Task<int> Completion => _completion.Task;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("AppServerBridge 只能启动一次。");
        }

        try
        {
            Approvals.BeginConnection();
            if (IsSharedSession)
            {
                _state.MarkConnectionState("connecting");
                var identity = await SharedServiceIdentity.ReadAndVerifyAsync(
                    _options.SharedManifestPath ?? throw new AgentConfigurationException("共享模式缺少身份文件。"),
                    _options.SharedEndpoint!, cancellationToken).ConfigureAwait(false);
                _sharedSocket = new ClientWebSocket();
                _sharedSocket.Options.Proxy = null;
                await _sharedSocket.ConnectAsync(_options.SharedEndpoint!, cancellationToken)
                    .WaitAsync(InitializeTimeout, cancellationToken).ConfigureAwait(false);
                identity.Verify();
                ServiceInstanceId = identity.ServiceInstanceId;
                _writerTask = SharedWriterLoopAsync(_lifetime.Token);
                _stdoutTask = SharedReaderLoopAsync(_lifetime.Token);
            }
            else
            {
                _state.MarkStarting();
                _process = StartProcess();
                ServiceInstanceId = $"stdio-{_process.Id}-{_process.StartTime.ToUniversalTime().Ticks}";
                _writerTask = WriterLoopAsync(_lifetime.Token);
                _stdoutTask = StdoutLoopAsync(_lifetime.Token);
                _stderrTask = StderrLoopAsync(_lifetime.Token);
                _monitorTask = MonitorProcessAsync();
            }
            var initializeReply = await SendInternalRequestAsync(
                "initialize",
                new
                {
                    clientInfo = new
                    {
                        name = "codex_control_agent",
                        title = "Codex Control Agent",
                        version = typeof(AppServerBridge).Assembly.GetName().Version?.ToString() ?? "0.1.0",
                    },
                    capabilities = new
                    {
                        experimentalApi = true,
                    },
                },
                InitializeTimeout,
                cancellationToken).ConfigureAwait(false);

            if (initializeReply.IsError)
            {
                throw new AgentException(
                    AgentErrorCodes.AppServerProtocolError,
                    "app-server 拒绝 initialize。返回错误正文未写入日志。");
            }

            _initializeResult = initializeReply.Payload.Clone();
            await QueueOutboundAsync(
                JsonRpcProtocol.BuildNotification("initialized"),
                cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _ready, 1);
            if (IsSharedSession) _state.MarkConnectionState("online");
            else _state.MarkIdle();
            _log.Info("app_server_ready", IsSharedSession
                ? "shared app-server client initialized; service lifecycle is independent"
                : "app-server stdio initialize/initialized succeeded");
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public bool TryRegisterClient(IAppServerClientSink client)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_clientGate)
        {
            if (IsSharedSession || _client is not null || !IsReady)
            {
                return false;
            }

            _client = client;
            _log.Info("tui_connected", "local Codex TUI connected");
            return true;
        }
    }

    public void UnregisterClient(IAppServerClientSink client)
    {
        lock (_clientGate)
        {
            if (ReferenceEquals(_client, client))
            {
                _client = null;
                _log.Info("tui_disconnected", "local Codex TUI disconnected");
            }
        }
    }

    public async ValueTask ForwardFromTuiAsync(string message, CancellationToken cancellationToken)
    {
        using var document = JsonRpcProtocol.Parse(message);
        var root = document.RootElement;
        if (JsonRpcProtocol.HasReservedBridgeId(root))
        {
            throw new AgentException(
                AgentErrorCodes.RequestIdNamespaceCollision,
                $"TUI Request ID 不得使用保留前缀 {JsonRpcProtocol.BridgeRequestIdPrefix}");
        }

        if (await Approvals.TryHandleTuiResponseAsync(
                root,
                message,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        _state.ApplyClientMessage(root);
        await QueueOutboundAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!IsReady)
        {
            throw new AgentException(
                AgentErrorCodes.AppServerDisconnected,
                "app-server bridge 尚未就绪。");
        }

        var reply = await SendInternalRequestAsync(
            method,
            parameters,
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (reply.IsError)
        {
            throw new AppServerRpcException(method, reply.Payload);
        }

        return reply.Payload.Clone();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0)
            {
                return;
            }

            Volatile.Write(ref _ready, 0);
            AbortClient("Agent is shutting down");
            _outbound.Writer.TryComplete();

            if (IsSharedSession)
            {
                // Never close stdin, kill, or wait on a shared executor's lifetime.
                _lifetime.Cancel();
                _sharedSocket?.Abort();
                await AwaitBackgroundTasksAsync().ConfigureAwait(false);
                Approvals.ResetConnection();
                _state.ResetConnectionApprovals();
                FailPending(new AgentException(AgentErrorCodes.AppServerDisconnected, "共享服务连接已断开，服务继续运行。"));
                _state.MarkConnectionState("offline");
                _completion.TrySetResult(0);
                _log.Info("shared_client_disconnected", "shared client disconnected; service left running");
                return;
            }

            if (_writerTask is not null)
            {
                await IgnoreCancellationAsync(
                    _writerTask.WaitAsync(WriterShutdownTimeout, CancellationToken.None)).ConfigureAwait(false);
            }

            try
            {
                _process?.StandardInput.Close();
            }
            catch (InvalidOperationException)
            {
                // 进程已退出，无需再次关闭 stdin。
            }

            if (_process is { HasExited: false } process)
            {
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(ProcessShutdownTimeout, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _log.Warning("app_server_kill", "app-server did not exit before shutdown timeout");
                    TryKillProcessTree(process);
                }
            }

            _lifetime.Cancel();
            await AwaitBackgroundTasksAsync().ConfigureAwait(false);
            FailPending(new AgentException(
                AgentErrorCodes.AppServerDisconnected,
                "app-server 已停止。"));
            _state.MarkOffline();
            _log.Info("app_server_stopped", "app-server stopped");
        }
        finally
        {
            _stopGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _process?.Dispose();
        _sharedSocket?.Dispose();
        _lifetime.Dispose();
        _stopGate.Dispose();
    }

    private Process StartProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.CodexPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        try
        {
            if (!process.Start())
            {
                throw new AgentException(
                    AgentErrorCodes.AppServerStartFailed,
                    "app-server 进程未能启动。");
            }
        }
        catch (Win32Exception exception)
        {
            process.Dispose();
            throw new AgentException(
                AgentErrorCodes.AppServerStartFailed,
                "无法启动 Codex app-server。",
                exception);
        }

        _log.Info("app_server_started", $"app-server started; pid={process.Id}");
        return process;
    }

    private async Task SharedWriterLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await _sharedSocket!.SendAsync(Encoding.UTF8.GetBytes(message).AsMemory(),
                    WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { SharedDisconnected(); }
    }

    private async Task SharedReaderLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult frame;
                do
                {
                    frame = await _sharedSocket!.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (frame.MessageType == WebSocketMessageType.Close) return;
                    if (frame.MessageType != WebSocketMessageType.Text ||
                        message.Length + frame.Count > Math.Max(_options.MaxMessageBytes, MinimumAppServerMessageBytes))
                        throw new AgentException(AgentErrorCodes.AppServerProtocolError, "共享服务返回无效或过大的消息。");
                    message.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);
                DispatchServerMessage(new UTF8Encoding(false, true).GetString(message.GetBuffer(), 0, checked((int)message.Length)));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            // No raw payload, endpoint credential or exception message is written to the log.
            _log.Warning("shared_client_read_failed", "shared client stream ended; reconnect requires identity verification");
        }
        finally { SharedDisconnected(); }
    }

    private void SharedDisconnected()
    {
        Volatile.Write(ref _ready, 0);
        _lifetime.Cancel();
        _sharedSocket?.Abort();
        _outbound.Writer.TryComplete();
        FailPending(new AgentException(AgentErrorCodes.AppServerDisconnected, "共享服务连接已中断，未停止服务。"));
        Approvals.ResetConnection();
        _state.ResetConnectionApprovals();
        _state.MarkConnectionState("offline");
        _completion.TrySetResult(Volatile.Read(ref _stopping) == 0 ? 1 : 0);
    }

    private async Task<RpcReply> SendInternalRequestAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var requestId = string.Concat(JsonRpcProtocol.BridgeRequestIdPrefix, Guid.NewGuid().ToString("D"));
        var completion = new TaskCompletionSource<RpcReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingInternal.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("内部 JSON-RPC Request ID 重复。");
        }

        try
        {
            await QueueOutboundAsync(
                JsonRpcProtocol.BuildRequest(requestId, method, parameters),
                cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingInternal.TryRemove(requestId, out _);
        }
    }

    private async ValueTask QueueOutboundAsync(string message, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(message) > _options.MaxMessageBytes)
        {
            throw new AgentException(
                AgentErrorCodes.AppServerProtocolError,
                "JSON-RPC 消息超过配置的最大长度。");
        }

        try
        {
            await _outbound.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException exception)
        {
            throw new AgentException(
                AgentErrorCodes.AppServerDisconnected,
                "app-server writer 已关闭。",
                exception);
        }
    }

    private async Task WriterLoopAsync(CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("app-server process is not started");
        try
        {
            await foreach (var message in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await process.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log.Error("app_server_write_failed", "app-server stdin writer failed", exception);
            _state.MarkFailed(AgentErrorCodes.AppServerDisconnected);
            TryKillProcessTree(process);
        }
    }

    private async Task StdoutLoopAsync(CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("app-server process is not started");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (Encoding.UTF8.GetByteCount(line) > Math.Max(
                        _options.MaxMessageBytes,
                        MinimumAppServerMessageBytes))
                {
                    throw new AgentException(
                        AgentErrorCodes.AppServerProtocolError,
                        "app-server stdout 消息超过配置上限。");
                }

                DispatchServerMessage(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log.Error("app_server_protocol_failed", "app-server stdout protocol loop failed", exception);
            _state.MarkFailed(AgentErrorCodes.AppServerProtocolError);
            FailPending(exception);
            TryKillProcessTree(process);
        }
    }

    private async Task StderrLoopAsync(CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("app-server process is not started");
        var lineCount = 0L;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                lineCount++;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (lineCount > 0)
            {
                _log.Info("app_server_stderr", $"app-server emitted {lineCount} stderr lines; content suppressed");
            }
        }
    }

    private void DispatchServerMessage(string line)
    {
        using var document = JsonRpcProtocol.Parse(line);
        var root = document.RootElement;
        if (JsonRpcProtocol.IsResponse(root))
        {
            var internalId = JsonRpcProtocol.GetStringId(root);
            if (internalId?.StartsWith(JsonRpcProtocol.BridgeRequestIdPrefix, StringComparison.Ordinal) == true)
            {
                if (_pendingInternal.TryRemove(internalId, out var completion))
                {
                    if (root.TryGetProperty("result", out var result))
                    {
                        completion.TrySetResult(new RpcReply(IsError: false, result.Clone()));
                    }
                    else if (root.TryGetProperty("error", out var error))
                    {
                        completion.TrySetResult(new RpcReply(IsError: true, error.Clone()));
                    }
                }

                return;
            }
        }

        Approvals.ObserveServerMessage(root);
        _state.ApplyServerMessage(root);
        InvokeServerMessageReceived(root.Clone());
        IAppServerClientSink? client;
        lock (_clientGate)
        {
            client = _client;
        }

        if (client is not null && !client.TryQueue(line))
        {
            _log.Warning("tui_backpressure", "TUI outbound queue is full; connection aborted");
            client.Abort(AgentErrorCodes.BridgeOverloaded);
        }
    }

    private void InvokeServerMessageReceived(JsonElement message)
    {
        var handlers = ServerMessageReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<JsonElement> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(message);
            }
            catch (Exception exception)
            {
                _log.Error("server_message_subscriber_failed", "server message subscriber failed", exception);
            }
        }
    }

    private async Task MonitorProcessAsync()
    {
        var process = _process ?? throw new InvalidOperationException("app-server process is not started");
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            _completion.TrySetResult(process.ExitCode);
            _lifetime.Cancel();
            FailPending(new AgentException(
                AgentErrorCodes.AppServerDisconnected,
                $"app-server 已退出，退出码 {process.ExitCode}。"));
            AbortClient(AgentErrorCodes.AppServerDisconnected);

            if (Volatile.Read(ref _stopping) == 0)
            {
                _state.MarkFailed(AgentErrorCodes.AppServerDisconnected);
                _log.Error("app_server_exited", $"app-server exited unexpectedly; code={process.ExitCode}");
            }
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
            _log.Error("app_server_monitor_failed", "app-server process monitor failed", exception);
        }
    }

    private void AbortClient(string reason)
    {
        IAppServerClientSink? client;
        lock (_clientGate)
        {
            client = _client;
            _client = null;
        }

        client?.Abort(reason);
    }

    private void FailPending(Exception exception)
    {
        foreach (var pending in _pendingInternal.ToArray())
        {
            if (_pendingInternal.TryRemove(pending.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private async Task AwaitBackgroundTasksAsync()
    {
        var tasks = new[] { _stdoutTask, _stderrTask, _writerTask, _monitorTask }
            .Where(static task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        await IgnoreCancellationAsync(Task.WhenAll(tasks)).ConfigureAwait(false);
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private sealed record RpcReply(bool IsError, JsonElement Payload);
}
