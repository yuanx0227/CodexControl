using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Configuration;
using CodexControl.Agent.Control;
using CodexControl.Agent.Diagnostics;
using CodexControl.Agent.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodexControl.Agent.Tests;

internal static class SharedTransportTests
{
    public static async Task RunAsync()
    {
        foreach (var value in new[] { "ws://localhost:9023", "ws://0.0.0.0:9023", "ws://127.0.0.2:9023", "wss://127.0.0.1:9023", "ws://127.0.0.1:9023/path", "ws://user:secret@127.0.0.1:9023", "ws://127.0.0.1:9023?token=secret" })
            Throws<AgentConfigurationException>(() => AgentOptions.ParseSharedEndpoint(value));
        Throws<AgentConfigurationException>(() => AgentOptions.Parse(["--shared-endpoint", "ws://127.0.0.1:9023"]));
        var root = Path.Combine(Path.GetTempPath(), "codex-control-shared-transport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Throws<AgentConfigurationException>(() => SharedProjectAuthorization.Validate(["relative-project"]));
        Throws<AgentConfigurationException>(() => SharedProjectAuthorization.Validate([Path.GetPathRoot(root)!]));
        Throws<AgentConfigurationException>(() => SharedProjectAuthorization.Validate([Path.Combine(root, "missing")]));
        var authorized = AgentOptions.Parse(["--shared-project-root", root, "--shared-project-root", root + Path.DirectorySeparatorChar]);
        Check(authorized.SharedProjectRoots.Count == 1 && authorized.SharedProjectRoots[0] == root, "local project authorization is normalized and deduplicated");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        var sockets = new ConcurrentDictionary<Guid, WebSocket>();
        var requests = new ConcurrentQueue<JsonElement>();
        var rejectCreatedPolicy = false;
        var createdHasTurn = false;
        var reportedWorkspace = root;
        const string createdThreadId = "01a10081-78c0-7851-a412-58e2b1febd88";
        object ThreadContext() => new
        {
            cwd = reportedWorkspace, approvalPolicy = "untrusted", approvalsReviewer = "user", runtimeWorkspaceRoots = new[] { reportedWorkspace },
            sandbox = new { type = "workspaceWrite", networkAccess = rejectCreatedPolicy, writableRoots = new[] { root }, excludeTmpdirEnvVar = true, excludeSlashTmp = true },
            thread = new { id = createdThreadId, cwd = reportedWorkspace, status = new { type = "idle" }, turns = Array.Empty<object>() },
        };
        app.Map("/", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var key = Guid.NewGuid();
            sockets[key] = socket;
            try
            {
                var bytes = new byte[1024 * 1024];
                while (socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult frame;
                    do
                    {
                        frame = await socket.ReceiveAsync(bytes, context.RequestAborted);
                        if (frame.MessageType == WebSocketMessageType.Close) return;
                        message.Write(bytes, 0, frame.Count);
                    } while (!frame.EndOfMessage);
                    using var doc = JsonDocument.Parse(message.ToArray());
                    var request = doc.RootElement;
                    if (!request.TryGetProperty("id", out var id)) continue;
                    requests.Enqueue(request.Clone());
                    var method = request.GetProperty("method").GetString();
                    if (method == "thread/start") createdHasTurn = false;
                    if (method == "thread/resume" && !createdHasTurn)
                    {
                        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                        {
                            id = id.Clone(), error = new { code = -32600, message = "no rollout found for thread id " + createdThreadId },
                        })), WebSocketMessageType.Text, true, context.RequestAborted);
                        continue;
                    }
                    if (method == "turn/start") createdHasTurn = true;
                    object result = request.GetProperty("method").GetString() switch
                    {
                        "thread/start" or "thread/read" or "thread/resume" => ThreadContext(),
                        "thread/turns/list" => new { data = Array.Empty<object>() },
                        "turn/start" => new { turn = new { id = "01a10081-78c0-7851-a412-58e2b1febd89" } },
                        _ => new { ok = true },
                    };
                    await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = id.Clone(), result })), WebSocketMessageType.Text, true, context.RequestAborted);
                    if (request.GetProperty("method").GetString() == "test/fragment")
                    {
                        var text = Encoding.UTF8.GetBytes("""{"method":"thread/status/changed","params":{"threadId":"shared-test","status":{"type":"active"}}}""");
                        await socket.SendAsync(text.AsMemory(0, 23), WebSocketMessageType.Text, false, context.RequestAborted);
                        await socket.SendAsync(text.AsMemory(23), WebSocketMessageType.Text, true, context.RequestAborted);
                    }
                }
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) { }
            finally { sockets.TryRemove(key, out _); }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var endpoint = AgentOptions.ParseSharedEndpoint(address.Replace("http:", "ws:", StringComparison.Ordinal));
        using var process = Process.GetCurrentProcess();
        var identity = new SharedServiceIdentity(Guid.NewGuid().ToString("N"), endpoint.ToString(), process.Id,
            process.StartTime.ToUniversalTime(), process.MainModule!.FileName, "codex-cli 0.0.0-fake", process.MainModule.FileVersionInfo.FileVersion);
        var manifest = Path.Combine(root, "manifest.json");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(identity, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var log = new AgentLog(Path.Combine(root, "logs"));
        var options = AgentOptions.ForTests("must-not-start-stdio.exe", root) with { SharedEndpoint = endpoint, SharedManifestPath = manifest, SharedProjectRoots = [root] };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var state = new CodexStateManager();
            using (var started = JsonDocument.Parse("""{"method":"turn/started","params":{"threadId":"prior-thread","turn":{"id":"prior-turn","status":"inProgress"}}}"""))
                state.ApplyServerMessage(started.RootElement);
            await using var first = new AppServerBridge(options, log, state);
            await using var second = new AppServerBridge(options, log, new CodexStateManager());
            await first.StartAsync(timeout.Token);
            await second.StartAsync(timeout.Token);
            Check(first.AuthorizedProjectRoots.SequenceEqual([root]), "bridge exposes only locally configured project roots");
            Check(first.IsSharedSession && first.ServiceInstanceId == second.ServiceInstanceId, "same verified service identity");
            Check(first.ConnectionEpoch != second.ConnectionEpoch, "connection epochs are distinct from service identity");
            Check(state.Snapshot.ActiveTurnId == "prior-turn", "initialize must not mark a running thread idle");
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            second.ServerMessageReceived += message => { if (message.GetProperty("method").GetString() == "thread/status/changed") observed.TrySetResult(); };
            await first.StopAsync(timeout.Token);
            identity.Verify();
            var reply = await second.SendRequestAsync("test/fragment", new { }, TimeSpan.FromSeconds(5), timeout.Token);
            Check(reply.GetProperty("ok").GetBoolean(), "other client still usable after first disconnect");
            await observed.Task.WaitAsync(timeout.Token);
            await using var reconnected = new AppServerBridge(options, log, new CodexStateManager());
            await reconnected.StartAsync(timeout.Token);
            Check(reconnected.ServiceInstanceId == second.ServiceInstanceId && reconnected.ConnectionEpoch != first.ConnectionEpoch, "reconnect preserves service instance but replaces connection epoch");
            Throws<AgentConfigurationException>(() => (identity with { StartedUtc = identity.StartedUtc.AddTicks(1) }).Verify());
            Throws<AgentConfigurationException>(() => (identity with { ProcessId = int.MaxValue }).Verify());
            var runtime = new SharedSessionRuntimeContext();
            var dispatcher = new RemoteControlDispatcher(second, new CodexStateManager(), runtime);
            var created = await dispatcher.StartThreadAsync(root, "simulated input", timeout.Token);
            Check(created.Succeeded, "authorized shared create validates effective scope before first prompt");
            Check(!requests.Any(value => value.GetProperty("method").GetString() == "thread/resume"),
                "a new Thread can start its first Turn before a persisted rollout exists");
            var creation = requests.Single(value => value.GetProperty("method").GetString() == "thread/start").GetProperty("params");
            var strict = creation.GetProperty("config").GetProperty("sandbox_workspace_write");
            Check(strict.GetProperty("exclude_tmpdir_env_var").GetBoolean() && strict.GetProperty("exclude_slash_tmp").GetBoolean() &&
                !strict.GetProperty("network_access").GetBoolean(), "new shared Thread explicitly excludes implicit temp/network grants");
            Check(requests.Where(value => value.GetProperty("method").GetString() == "thread/resume").All(value =>
                value.GetProperty("params").EnumerateObject().All(property => property.Name is "threadId" or "excludeTurns")), "shared observation does not override permissions");
            var restored = new RemoteControlDispatcher(reconnected, new CodexStateManager(), runtime);
            var joined = await restored.SharedSessions.JoinAsync(createdThreadId, timeout.Token, allowJoin: false);
            Check(joined.PolicyReason is null, "view-only can restore a prior authorized observation on a new connection to the same service");
            await ThrowsAgentAsync("STEER_PERMISSION_REQUIRED", () => restored.SharedSessions.JoinAsync(
                "01a10081-78c0-7851-a412-58e2b1febd90", timeout.Token, allowJoin: false));
            var otherManifest = Path.Combine(root, "other-service.json");
            await File.WriteAllTextAsync(otherManifest, JsonSerializer.Serialize(identity with { ServiceInstanceId = Guid.NewGuid().ToString("N") }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await using (var otherService = new AppServerBridge(options with { SharedManifestPath = otherManifest }, log, new CodexStateManager()))
            {
                await otherService.StartAsync(timeout.Token);
                var otherDispatcher = new RemoteControlDispatcher(otherService, new CodexStateManager(), runtime);
                await ThrowsAgentAsync("STEER_PERMISSION_REQUIRED", () => otherDispatcher.SharedSessions.JoinAsync(createdThreadId, timeout.Token, allowJoin: false));
            }
            reportedWorkspace = Directory.CreateDirectory(Path.Combine(root, "changed-project")).FullName;
            await ThrowsAgentAsync("PROJECT_SCOPE_CHANGED", () => new RemoteControlDispatcher(reconnected, new CodexStateManager(), runtime)
                .SharedSessions.JoinAsync(createdThreadId, timeout.Token, allowJoin: false));
            reportedWorkspace = root;
            var sentTurns = requests.Count(value => value.GetProperty("method").GetString() == "turn/start");
            rejectCreatedPolicy = true;
            var rejected = await dispatcher.StartThreadAsync(root, "must not be sent", timeout.Token);
            Check(rejected.ErrorCode == "ADDITIONAL_PERMISSION_REQUIRES_REVIEW" &&
                requests.Count(value => value.GetProperty("method").GetString() == "turn/start") == sentTurns,
                "server-returned extra permission blocks prompt even when create RPC accepted");
        }
        finally
        {
            foreach (var socket in sockets.Values) socket.Abort();
            await app.StopAsync();
            // Only this test's generated metadata/log directory; production configuration is untouched.
            log.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static async Task ThrowsAgentAsync(string code, Func<Task<(JsonElement Context, string? PolicyReason)>> action)
    {
        try { await action(); }
        catch (AgentException exception) when (exception.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
}
