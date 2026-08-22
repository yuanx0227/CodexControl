using System.Text.Json;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Configuration;
using CodexControl.Agent.Control;
using CodexControl.Agent.Diagnostics;
using CodexControl.Agent.Proxy;
using CodexControl.Agent.Relay;
using CodexControl.Agent.Security;
using CodexControl.Agent.State;

namespace CodexControl.Agent;

public static class AgentProgram
{
    public static async Task<int> RunAsync(string[] args)
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

        try
        {
            using var log = new AgentLog(options.LogDirectory);
            log.Info("startup", ".NET 8 CodexControlAgent starting");

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

            using var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;

            var state = new CodexStateManager();
            await using var bridge = new AppServerBridge(options, log, state);
            await using var proxy = new LocalCodexProxyServer(options, bridge, log);
            DeviceIdentity? identity = null;
            RelayClient? relayClient = null;

            try
            {
                await bridge.StartAsync(shutdown.Token).ConfigureAwait(false);
                await proxy.StartAsync(shutdown.Token).ConfigureAwait(false);

                if (options.RelayUrl is not null)
                {
                    identity = DeviceIdentity.LoadOrCreate(options.DataDirectory, options.DeviceName);
                    var dispatcher = new RemoteControlDispatcher(bridge, state);
                    relayClient = new RelayClient(options, identity, state, bridge, dispatcher, log);
                    relayClient.Start();
                    Console.WriteLine($"DEVICE {identity.DeviceId} {identity.Name}");
                    Console.WriteLine($"RELAY {options.RelayUrl}");
                    if (options.CreatePairing)
                    {
                        await relayClient.WaitUntilAuthenticatedAsync(shutdown.Token).ConfigureAwait(false);
                        var pairing = await relayClient.CreatePairingAsync(shutdown.Token).ConfigureAwait(false);
                        Console.WriteLine($"PAIRING_CODE {pairing.Code} EXPIRES_AT {pairing.ExpiresAt}");
                    }
                }

                Console.WriteLine($"READY {proxy.WebSocketUri}");
                Console.WriteLine($"CONNECT codex --remote {proxy.WebSocketUri}");

                var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
                var completed = await Task.WhenAny(cancellationTask, bridge.Completion).ConfigureAwait(false);
                if (ReferenceEquals(completed, bridge.Completion) && !shutdown.IsCancellationRequested)
                {
                    var exitCode = await bridge.Completion.ConfigureAwait(false);
                    log.Error("agent_stopping", $"app-server exited unexpectedly; code={exitCode}");
                    return 5;
                }

                return 0;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return 0;
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

                identity?.Dispose();
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
                finally
                {
                    Console.CancelKeyPress -= cancelHandler;
                    log.Info("shutdown", "CodexControlAgent stopped");
                }
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
}
