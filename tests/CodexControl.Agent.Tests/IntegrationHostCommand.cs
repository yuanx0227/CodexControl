using System.Globalization;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Configuration;
using CodexControl.Agent.Diagnostics;
using CodexControl.Agent.Proxy;
using CodexControl.Agent.State;

namespace CodexControl.Agent.Tests;

internal static class IntegrationHostCommand
{
    public static bool CanHandle(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] == "--host-real-proxy";

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count is not (5 or 6) ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            Console.Error.WriteLine(
                "usage: --host-real-proxy <codex-path> <port> <seconds> <log-dir> [stop-file]");
            return 2;
        }

        var options = AgentOptions.ForTests(args[1], args[4], port);
        using var log = new AgentLog(options.LogDirectory);
        var state = new CodexStateManager();
        await using var bridge = new AppServerBridge(options, log, state);
        await using var proxy = new LocalCodexProxyServer(options, bridge, log);
        try
        {
            await bridge.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await proxy.StartAsync(CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"READY {proxy.WebSocketUri}");
            Console.Out.Flush();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(seconds));
            if (args.Count == 6)
            {
                await Task.WhenAny(timeoutTask, WaitForStopFileAsync(args[5])).ConfigureAwait(false);
            }
            else
            {
                await timeoutTask.ConfigureAwait(false);
            }

            return 0;
        }
        finally
        {
            await proxy.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await bridge.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task WaitForStopFileAsync(string stopFile)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (!File.Exists(stopFile) && await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
        }
    }
}
