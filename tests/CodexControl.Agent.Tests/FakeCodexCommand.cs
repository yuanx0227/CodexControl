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
                var includeRemoteHistory = message.TryGetProperty("params", out var listParams) &&
                                           listParams.TryGetProperty("sourceKinds", out _);
                using var resultDocument = JsonDocument.Parse(includeRemoteHistory
                    ? """
                      {
                        "data":[
                          {
                            "id":"thr-history-1",
                            "name":"历史测试会话",
                            "preview":"修复登录模块",
                            "cwd":"D:\\Projects\\MES",
                            "createdAt":1730831111,
                            "updatedAt":1730832222,
                            "status":{"type":"notLoaded"},
                            "source":{"kind":"appServer"}
                          }
                        ],
                        "nextCursor":null
                      }
                      """
                    : """{"data":[],"nextCursor":null}""");
                await WriteAsync(JsonRpcProtocol.BuildResultResponse(id, resultDocument.RootElement))
                    .ConfigureAwait(false);
                continue;
            }

            if (method == "thread/read")
            {
                var readParams = message.GetProperty("params");
                var threadId = readParams.GetProperty("threadId").GetString();
                var includeTurns = readParams.TryGetProperty("includeTurns", out var includeTurnsValue) &&
                                   includeTurnsValue.ValueKind == JsonValueKind.True;
                var turns = includeTurns
                    ? """
                      [{
                        "id":"turn-history-1",
                        "status":"completed",
                        "items":[
                          {
                            "id":"item-history-user",
                            "type":"userMessage",
                            "content":[{"type":"text","text":"修复登录模块"}]
                          },
                          {
                            "id":"item-history-agent",
                            "type":"agentMessage",
                            "text":"登录模块已修复",
                            "phase":"final_answer"
                          }
                        ]
                      }]
                      """
                    : "[]";
                using var resultDocument = JsonDocument.Parse(
                    $$"""
                    {
                      "thread":{
                        "id":{{JsonSerializer.Serialize(threadId)}},
                        "name":"历史测试会话",
                        "cwd":"D:\\Projects\\MES",
                        "turns":{{turns}}
                      }
                    }
                    """);
                await WriteAsync(JsonRpcProtocol.BuildResultResponse(id, resultDocument.RootElement))
                    .ConfigureAwait(false);
                continue;
            }

            if (method == "thread/items/list")
            {
                var threadId = message.GetProperty("params").GetProperty("threadId").GetString();
                if (threadId == "thr-history-legacy")
                {
                    await WriteAsync(JsonRpcProtocol.BuildErrorResponse(
                        id,
                        -32601,
                        "thread/items/list is not supported yet")).ConfigureAwait(false);
                    continue;
                }

                using var resultDocument = JsonDocument.Parse("""
                    {
                      "data":[
                        {
                          "turnId":"turn-history-1",
                          "item":{
                            "id":"item-history-agent",
                            "type":"agentMessage",
                            "text":"登录模块已修复",
                            "phase":"final_answer"
                          }
                        },
                        {
                          "turnId":"turn-history-1",
                          "item":{
                            "id":"item-history-user",
                            "type":"userMessage",
                            "content":[{"type":"text","text":"修复登录模块"}]
                          }
                        }
                      ],
                      "nextCursor":null
                    }
                    """);
                await WriteAsync(JsonRpcProtocol.BuildResultResponse(id, resultDocument.RootElement))
                    .ConfigureAwait(false);
                continue;
            }

            if (method == "thread/start")
            {
                using var resultDocument = JsonDocument.Parse("""
                    {"thread":{"id":"thr-created","cwd":"D:\\Projects\\New"}}
                    """);
                await WriteAsync(JsonRpcProtocol.BuildResultResponse(id, resultDocument.RootElement))
                    .ConfigureAwait(false);
                await WriteAsync("""
                    {"method":"thread/started","params":{"thread":{"id":"thr-created","cwd":"D:\\Projects\\New"}}}
                    """).ConfigureAwait(false);
                continue;
            }

            if (method == "thread/resume")
            {
                var threadId = message.GetProperty("params").GetProperty("threadId").GetString();
                using var resultDocument = JsonDocument.Parse(
                    $"{{\"thread\":{{\"id\":{JsonSerializer.Serialize(threadId)}}}}}");
                await WriteAsync(JsonRpcProtocol.BuildResultResponse(id, resultDocument.RootElement))
                    .ConfigureAwait(false);
                await WriteAsync(JsonSerializer.Serialize(new
                {
                    method = "thread/started",
                    @params = new { thread = new { id = threadId } },
                })).ConfigureAwait(false);
                continue;
            }

            if (method == "turn/start")
            {
                var threadId = message.GetProperty("params").GetProperty("threadId").GetString() ?? "thr-unknown";
                var turnId = string.Equals(threadId, "thr-created", StringComparison.Ordinal)
                    ? "turn-created"
                    : "turn-resumed";
                using var resultDocument = JsonDocument.Parse(
                    $"{{\"turn\":{{\"id\":{JsonSerializer.Serialize(turnId)},\"status\":\"inProgress\"}}}}");
                await WriteAsync(JsonRpcProtocol.BuildResultResponse(id, resultDocument.RootElement))
                    .ConfigureAwait(false);
                await WriteAsync(JsonSerializer.Serialize(new
                {
                    method = "turn/started",
                    @params = new
                    {
                        threadId,
                        turn = new { id = turnId, status = "inProgress" },
                    },
                })).ConfigureAwait(false);
                await WriteAsync(JsonSerializer.Serialize(new
                {
                    method = "turn/completed",
                    @params = new
                    {
                        threadId,
                        turn = new { id = turnId, status = "completed" },
                    },
                })).ConfigureAwait(false);
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
