using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Configuration;
using CodexControl.Agent.Control;
using CodexControl.Agent.Diagnostics;
using CodexControl.Agent.Proxy;
using CodexControl.Agent.State;

namespace CodexControl.Agent.Tests;

internal static class SoakCommand
{
    public static bool CanHandle(IReadOnlyList<string> args) => args.Count > 0 && args[0] == "--soak";

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count != 2 || !int.TryParse(args[1], out var seconds) || seconds < 1)
        {
            Console.Error.WriteLine("usage: --soak <seconds>");
            return 2;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            string.Concat("codex-control-soak-", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        var nextProgress = DateTimeOffset.UtcNow.AddMinutes(1);
        var iterations = 0;
        var restarts = 0;
        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                var options = AgentOptions.ForTests(
                    GetExecutablePath(),
                    Path.Combine(root, "logs"));
                using var log = new AgentLog(options.LogDirectory, maxBytes: 512 * 1024, retainedFiles: 3);
                var state = new CodexStateManager();
                await using var bridge = new AppServerBridge(options, log, state);
                await using var proxy = new LocalCodexProxyServer(options, bridge, log);
                await bridge.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await proxy.StartAsync(CancellationToken.None).ConfigureAwait(false);
                var dispatcher = new RemoteControlDispatcher(bridge, state);
                restarts++;

                for (var cycle = 0; cycle < 60 && DateTimeOffset.UtcNow < deadline; cycle++)
                {
                    var initializeId = 100_000 + iterations * 2;
                    var listId = initializeId + 1;
                    using var client = new ClientWebSocket();
                    await client.ConnectAsync(proxy.WebSocketUri, CancellationToken.None).ConfigureAwait(false);
                    await SendAsync(client,
                        $"{{\"method\":\"initialize\",\"id\":{initializeId},\"params\":{{\"clientInfo\":{{\"name\":\"soak-tui\",\"version\":\"1.0\"}},\"capabilities\":{{\"experimentalApi\":true}}}}}}")
                        .ConfigureAwait(false);
                    _ = await ReceiveResponseAsync(client, initializeId).ConfigureAwait(false);
                    await SendAsync(client, """{"method":"initialized"}""").ConfigureAwait(false);
                    await SendAsync(client, $"{{\"method\":\"thread/list\",\"id\":{listId},\"params\":{{\"limit\":1}}}}")
                        .ConfigureAwait(false);
                    _ = await ReceiveResponseAsync(client, listId).ConfigureAwait(false);
                    await client.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "soak reconnect",
                        CancellationToken.None).ConfigureAwait(false);

                    Apply(state, """
                        {"method":"turn/started","params":{"threadId":"thr-1","turn":{"id":"turn-1"}}}
                        """);
                    var steer = await dispatcher.SteerAsync(
                        "thr-1",
                        "turn-1",
                        "soak steer",
                        CancellationToken.None).ConfigureAwait(false);
                    if (!steer.Succeeded) throw new InvalidOperationException("Soak steer failed");
                    var interrupt = await dispatcher.InterruptAsync(
                        "thr-1",
                        "turn-1",
                        CancellationToken.None).ConfigureAwait(false);
                    if (!interrupt.Succeeded) throw new InvalidOperationException("Soak interrupt failed");
                    iterations++;

                    if (DateTimeOffset.UtcNow >= nextProgress)
                    {
                        Console.WriteLine($"SOAK_PROGRESS iterations={iterations} restarts={restarts}");
                        Console.Out.Flush();
                        nextProgress = nextProgress.AddMinutes(1);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }

                await proxy.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await bridge.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            Console.WriteLine($"SOAK_PASS seconds={seconds} iterations={iterations} restarts={restarts}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SOAK_FAIL iterations={iterations} restarts={restarts}: {exception}");
            return 1;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReceiveResponseAsync(ClientWebSocket socket, int id)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var buffer = new byte[64 * 1024];
            var result = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
            if (document.RootElement.TryGetProperty("id", out var responseId) &&
                responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == id)
            {
                return document;
            }

            document.Dispose();
        }

        throw new TimeoutException($"Soak response id={id} was not received.");
    }

    private static void Apply(CodexStateManager state, string json)
    {
        using var document = JsonRpcProtocol.Parse(json);
        state.ApplyServerMessage(document.RootElement);
    }

    private static string GetExecutablePath()
    {
        var appHost = Path.Combine(AppContext.BaseDirectory, "CodexControlAgentTests.exe");
        return File.Exists(appHost)
            ? appHost
            : Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path unavailable.");
    }
}
