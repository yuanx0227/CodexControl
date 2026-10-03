using System.Text.Json;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Configuration;
using CodexControl.Agent.Control;
using CodexControl.Agent.Diagnostics;
using CodexControl.Agent.Desktop;
using CodexControl.Agent.Proxy;
using CodexControl.Agent.Relay;
using CodexControl.Agent.Security;
using CodexControl.Agent.State;
using CodexControl.Agent.Lifecycle;

namespace CodexControl.Agent;

public static class AgentProgram
{
    private static readonly int[] AppServerRestartSeconds = [1, 2, 5, 10, 30];

    public static async Task<int> RunAsync(string[] args)
    {
        var startHidden = args.Length == 1 && string.Equals(args[0], "--background", StringComparison.Ordinal);
        if (args.Length == 0 || startHidden)
        {
            return AgentDesktopApplication.Run(startHidden);
        }

        ConsoleBridge.AttachToParent();
        var headlessArgs = args
            .Where(static argument => !string.Equals(argument, "--headless", StringComparison.Ordinal))
            .ToArray();
        return await RunHeadlessAsync(headlessArgs).ConfigureAwait(false);
    }

    private static async Task<int> RunHeadlessAsync(string[] args)
    {
        AgentOptions options;
        try
        {
            options = AgentOptions.Parse(args);
        }
        catch (AgentConfigurationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(AgentOptions.HelpText);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(AgentOptions.HelpText);
            return 0;
        }

        if (options.SharedHost)
        {
            try { return SharedRuntimeHost.Run(options); }
            catch (Exception exception)
            {
                MessageBox.Show(exception is AgentConfigurationException ? exception.Message : "共享宿主启动失败。", "Codex Control");
                return 3;
            }
        }

        try
        {
            using var log = new AgentLog(options.LogDirectory);
            log.Info("startup", ".NET 8 CodexControlAgent starting");

            if (options.SharedDesktop)
                return await SharedRuntimeHost.LaunchDesktopAsync(options, CancellationToken.None).ConfigureAwait(false);

            if (options.SharedEndpoint is not null)
            {
                var sharedIdentity = await SharedServiceIdentity.ReadAndVerifyAsync(options.SharedManifestPath!, options.SharedEndpoint, CancellationToken.None).ConfigureAwait(false);
                options = options with { CodexPath = sharedIdentity.ImagePath };
                if (options.ProbeOnly)
                {
                    Console.WriteLine(JsonSerializer.Serialize(sharedIdentity));
                    return 0;
                }
            }
            else
            {

                var runtime = await CodexRuntimeResolver.ResolveAsync(
                    options.CodexPath,
                    options.DataDirectory,
                    CancellationToken.None).ConfigureAwait(false);
                options = options with { CodexPath = runtime.ExecutablePath };
                log.Info(
                    "codex_runtime_resolved",
                    $"Codex runtime source={runtime.Source}; staged={runtime.WasStaged}; " +
                    $"package={runtime.DesktopPackageName ?? "none"}");
                if (runtime.Source == CodexRuntimeSource.DesktopBundle)
                {
                    Console.WriteLine(
                        $"CODEX_RUNTIME DESKTOP {runtime.DesktopPackageName} " +
                        $"{(runtime.WasStaged ? "STAGED" : "REUSED")}");
                }

                var probe = await CodexExecutableProbe.ProbeAsync(
                    options.CodexPath,
                    CancellationToken.None).ConfigureAwait(false);
                log.Info("codex_probe_succeeded", $"Codex capability probe succeeded; version={probe.Version}");

                if (options.ProbeOnly)
                {
                    Console.WriteLine(JsonSerializer.Serialize(probe, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                    }));
                    return 0;
                }
            }

            using var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;

            DeviceIdentity? identity = options.RelayUrl is null
                ? null
                : DeviceIdentity.LoadOrCreate(options.DataDirectory, options.DeviceName);
            var restartAttempt = 0;
            var pairingCreated = false;
            var sharedRuntime = new SharedSessionRuntimeContext();
            try
            {
                while (!shutdown.IsCancellationRequested)
                {
                    var sessionStartedAt = DateTimeOffset.UtcNow;
                    var state = new CodexStateManager();
                    await using var bridge = new AppServerBridge(options, log, state);
                    await using var proxy = new LocalCodexProxyServer(options, bridge, log);
                    RelayClient? relayClient = null;
                    try
                    {
                        await bridge.StartAsync(shutdown.Token).ConfigureAwait(false);
                        if (!bridge.IsSharedSession) await proxy.StartAsync(shutdown.Token).ConfigureAwait(false);

                        if (options.RelayUrl is not null && identity is not null)
                        {
                            var dispatcher = new RemoteControlDispatcher(bridge, state, sharedRuntime);
                            relayClient = new RelayClient(options, identity, state, bridge, dispatcher, log, runtimeContext: sharedRuntime);
                            relayClient.Start();
                            Console.WriteLine($"DEVICE {identity.DeviceId} {identity.Name}");
                            Console.WriteLine($"RELAY {options.RelayUrl}");
                            if (options.CreatePairing && !pairingCreated)
                            {
                                await relayClient.WaitUntilAuthenticatedAsync(shutdown.Token).ConfigureAwait(false);
                                var pairing = await relayClient.CreatePairingAsync(shutdown.Token).ConfigureAwait(false);
                                pairingCreated = true;
                                Console.WriteLine($"PAIRING_CODE {pairing.Code} EXPIRES_AT {pairing.ExpiresAt}");
                            }
                        }

                        if (bridge.IsSharedSession)
                            Console.WriteLine($"READY SHARED {bridge.ServiceInstanceId}");
                        else
                        {
                            Console.WriteLine($"READY {proxy.WebSocketUri}");
                            Console.WriteLine($"CONNECT & {ToPowerShellLiteral(options.CodexPath)} --remote " +
                                ToPowerShellLiteral(proxy.WebSocketUri.ToString()));
                        }

                        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
                        var completed = await Task.WhenAny(cancellationTask, bridge.Completion).ConfigureAwait(false);
                        if (!ReferenceEquals(completed, bridge.Completion) || shutdown.IsCancellationRequested)
                        {
                            return 0;
                        }

                        var exitCode = await bridge.Completion.ConfigureAwait(false);
                        log.Error("app_server_session_failed", $"app-server exited unexpectedly; code={exitCode}");
                    }
                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                    {
                        return 0;
                    }
                    catch (Exception exception)
                    {
                        log.Error("agent_session_failed", "Agent session failed and will be restarted", exception);
                    }
                    finally
                    {
                        if (relayClient is not null)
                        {
                            try
                            {
                                await relayClient.DisposeAsync().ConfigureAwait(false);
                            }
                            catch (Exception exception)
                            {
                                log.Error("relay_stop_failed", "Relay client shutdown failed", exception);
                            }
                        }

                        try
                        {
                            using var proxyStopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                            await proxy.StopAsync(proxyStopTimeout.Token).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            log.Error("local_proxy_stop_failed", "local proxy shutdown failed", exception);
                        }

                        try
                        {
                            using var bridgeStopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                            await bridge.StopAsync(bridgeStopTimeout.Token).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            log.Error("app_server_stop_failed", "app-server bridge shutdown failed", exception);
                        }
                    }

                    if (shutdown.IsCancellationRequested)
                    {
                        return 0;
                    }

                    if (DateTimeOffset.UtcNow - sessionStartedAt > TimeSpan.FromMinutes(1))
                    {
                        restartAttempt = 0;
                    }

                    var restartSeconds = AppServerRestartSeconds[
                        Math.Min(restartAttempt++, AppServerRestartSeconds.Length - 1)];
                    log.Warning(
                        "agent_session_restarting",
                        $"Restarting app-server session in {restartSeconds}s; attempt={restartAttempt}");
                    await Task.Delay(TimeSpan.FromSeconds(restartSeconds), shutdown.Token).ConfigureAwait(false);
                }

                return 0;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return 0;
            }
            finally
            {
                identity?.Dispose();
                Console.CancelKeyPress -= cancelHandler;
                log.Info("shutdown", "CodexControlAgent stopped");
            }
        }
        catch (AgentException exception)
        {
            Console.Error.WriteLine($"{exception.Code}: {exception.Message}");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"INTERNAL_ERROR: {exception.Message}");
            return 1;
        }
    }

    private static string ToPowerShellLiteral(string value) =>
        string.Concat("'", value.Replace("'", "''", StringComparison.Ordinal), "'");
}
