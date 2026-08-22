using System.Text.Json;
using CodexControl.Agent.Codex;

namespace CodexControl.Agent.Tests;

internal static class FakeCodexCommand
{
    public static bool CanHandle(IReadOnlyList<string> args) =>
        args.Count > 0 &&
        (args[0] is "--version" or "--help" or "app-server");

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args[0] == "--version")
        {
            Console.WriteLine("codex-cli 0.0.0-fake");
            return 0;
        }

        if (args[0] == "--help")
        {
            Console.WriteLine("Codex CLI fake --remote <ADDR>");
            return 0;
        }

        if (args.Count > 1 && args[1] == "--help")
        {
            Console.WriteLine("codex app-server --listen <URL> supports stdio://");
            return 0;
        }

        return await RunAppServerAsync().ConfigureAwait(false);
    }

    private static async Task<int> RunAppServerAsync()
    {
        var initialized = false;
        while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            using var document = JsonRpcProtocol.Parse(line);
            var message = document.RootElement;
            if (!JsonRpcProtocol.TryGetMethod(message, out var method))
            {
                if (JsonRpcProtocol.IsResponse(message) &&
                    JsonRpcProtocol.TryGetId(message, out var responseId) &&
                    responseId.ValueKind == JsonValueKind.String &&
                    responseId.GetString() == "server-approval-1")
                {
                    await WriteAsync("""
                        {"method":"serverRequest/resolved","params":{"threadId":"thr-1","requestId":"server-approval-1"}}
                        """).ConfigureAwait(false);
                }

                continue;
            }

            if (method == "initialize" && JsonRpcProtocol.TryGetId(message, out var initializeId))
            {
                var experimentalApi = message.TryGetProperty("params", out var initializeParams) &&
                                      initializeParams.TryGetProperty("capabilities", out var capabilities) &&
                                      capabilities.TryGetProperty("experimentalApi", out var experimental) &&
                                      experimental.ValueKind == JsonValueKind.True;
                if (!experimentalApi)
                {
                    await WriteAsync(JsonRpcProtocol.BuildErrorResponse(
                        initializeId,
                        -32602,
                        "experimentalApi capability required by fake TUI contract")).ConfigureAwait(false);
                    continue;
                }

                using var resultDocument = JsonDocument.Parse("""
                    {
                      "userAgent": "Codex Fake/0.0.0",
                      "codexHome": "C:\\fake-codex-home",
                      "platformFamily": "windows",
                      "platformOs": "windows"
                    }
                    """);
                await WriteAsync(JsonRpcProtocol.BuildResultResponse(
                    initializeId,
                    resultDocument.RootElement)).ConfigureAwait(false);
                continue;
            }

            if (method == "initialized")
            {
                initialized = true;
                await WriteAsync("""
                    {"method":"remoteControl/status/changed","params":{"status":"disabled","serverName":"fake","installationId":"fake-installation","environmentId":null}}
                    """).ConfigureAwait(false);
                continue;
            }

            if (!JsonRpcProtocol.TryGetId(message, out var id))
            {
                continue;
            }

            if (!initialized)
            {
                await WriteAsync(JsonRpcProtocol.BuildErrorResponse(id, -32600, "Not initialized"))
                    .ConfigureAwait(false);
                continue;
            }

            if (method == "thread/list")
            {
                using var resultDocument = JsonDocument.Parse("""
                    {"data":[],"nextCursor":null}
                    """);
                await WriteAsync(JsonRpcProtocol.BuildResultResponse(id, resultDocument.RootElement))
                    .ConfigureAwait(false);
                continue;
            }

            if (method == "turn/steer")
            {
                using var resultDocument = JsonDocument.Parse("""{"turnId":"turn-1"}""");
                await WriteAsync(JsonRpcProtocol.BuildResultResponse(id, resultDocument.RootElement))
                    .ConfigureAwait(false);
                continue;
            }

            if (method == "turn/interrupt")
            {
                using var resultDocument = JsonDocument.Parse("""{}""");
                await WriteAsync(JsonRpcProtocol.BuildResultResponse(id, resultDocument.RootElement))
                    .ConfigureAwait(false);
                await WriteAsync("""
                    {"method":"turn/completed","params":{"threadId":"thr-1","turn":{"id":"turn-1","status":"interrupted"}}}
                    """).ConfigureAwait(false);
                continue;
            }

            if (method == "test/emitApproval")
            {
                using var resultDocument = JsonDocument.Parse("""{}""");
                await WriteAsync(JsonRpcProtocol.BuildResultResponse(id, resultDocument.RootElement))
                    .ConfigureAwait(false);
                await WriteAsync("""
                    {"method":"item/commandExecution/requestApproval","id":"server-approval-1","params":{"threadId":"thr-1","turnId":"turn-1","itemId":"item-1","command":"dotnet test","cwd":"D:\\work","reason":"test","availableDecisions":["accept","decline"],"startedAtMs":1}}
                    """).ConfigureAwait(false);
                continue;
            }

            using (var resultDocument = JsonDocument.Parse($"{{\"echoMethod\":{JsonSerializer.Serialize(method)}}}"))
            {
                await WriteAsync(JsonRpcProtocol.BuildResultResponse(id, resultDocument.RootElement))
                    .ConfigureAwait(false);
            }
        }

        return 0;
    }

    private static async Task WriteAsync(string message)
    {
        await Console.Out.WriteLineAsync(message).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }
}
