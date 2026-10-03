using System.Collections.Concurrent;
using System.Text.Json;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Configuration;
using CodexControl.Agent.Control;
using CodexControl.Agent.Diagnostics;
using CodexControl.Agent.State;
using CodexControl.Protocol;

namespace CodexControl.Agent.Tests;

internal static class SharedSessionRealTests
{
    public static async Task RunAsync()
    {
        var manifest = Environment.GetEnvironmentVariable("CODEX_CONTROL_SHARED_TEST_MANIFEST");
        if (string.IsNullOrWhiteSpace(manifest)) throw new TestRunner.SkipTestException("Explicit shared test manifest not supplied.");
        using var source = JsonDocument.Parse(File.ReadAllText(manifest));
        var endpoint = source.RootElement.GetProperty("endpoint").GetString()!;
        var root = Path.Combine(Path.GetDirectoryName(manifest)!, "product-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var options = AgentOptions.Parse(["--shared-endpoint", endpoint, "--shared-manifest", manifest,
            "--data-dir", Path.Combine(root, "data"), "--log-dir", Path.Combine(root, "logs"), "--shared-project-root", workspace]);
        using var log = new AgentLog(Path.Combine(root, "logs"));
        var stateA = new CodexStateManager();
        var stateB = new CodexStateManager();
        await using var a = new AppServerBridge(options, log, stateA);
        await using var b = new AppServerBridge(options, log, stateB);
        var events = new ConcurrentQueue<(string Method, string? Turn)>();
        var stream = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? threadId = null;
        string? turnId = null;
        b.ServerMessageReceived += message =>
        {
            if (!message.TryGetProperty("method", out var m) || !message.TryGetProperty("params", out var p) ||
                !p.TryGetProperty("threadId", out var tid) || tid.GetString() != threadId) return;
            var method = m.GetString()!;
            var turn = p.TryGetProperty("turnId", out var t) ? t.GetString() :
                p.TryGetProperty("turn", out var tr) ? tr.GetProperty("id").GetString() : null;
            events.Enqueue((method, turn));
            if (method == "item/agentMessage/delta" && turn is not null) stream.TrySetResult(turn);
            if (method == "turn/completed") completed.TrySetResult(p.GetProperty("turn").GetProperty("status").GetString()!);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await a.StartAsync(timeout.Token);
            await b.StartAsync(timeout.Token);
            Assert(a.ServiceInstanceId == b.ServiceInstanceId, "same verified service identity");
            var created = await a.SendRequestAsync("thread/start", new
            {
                cwd = workspace, runtimeWorkspaceRoots = new[] { workspace }, ephemeral = false, historyMode = "paginated",
                approvalPolicy = "untrusted", approvalsReviewer = "user", sandbox = "workspace-write",
                config = new { sandbox_workspace_write = new { writable_roots = new[] { workspace }, network_access = false,
                    exclude_tmpdir_env_var = true, exclude_slash_tmp = true } },
            }, TimeSpan.FromSeconds(30), timeout.Token);
            threadId = created.GetProperty("thread").GetProperty("id").GetString()!;
            await a.SendRequestAsync("thread/name/set", new { threadId, name = "CC_SHARED_PRODUCT_" + Path.GetFileName(root) }, TimeSpan.FromSeconds(15), timeout.Token);
            await a.SendRequestAsync("thread/read", new { threadId, includeTurns = true }, TimeSpan.FromSeconds(15), timeout.Token);
            var dispatcherA = new RemoteControlDispatcher(a, stateA);
            var dispatcherB = new RemoteControlDispatcher(b, stateB);
            var joined = await dispatcherB.SharedSessions.JoinAsync(threadId, timeout.Token);
            Assert(joined.PolicyReason is null, "effective policy accepted without overrides");
            var started = await dispatcherA.SharedSessions.SendAsync(threadId,
                "不要调用任何工具。直接写一篇800字中文海洋科普文章，持续输出正文。", null, timeout.Token);
            Assert(started.Succeeded, "product send accepted");
            turnId = started.Result!.Value.GetProperty("turnId").GetString();
            Assert(await stream.Task.WaitAsync(timeout.Token) == turnId, "second product client receives same Turn stream");
            await a.DisposeAsync();
            var marker = "PRODUCT_SHARED_OK_" + Guid.NewGuid().ToString("N");
            var steered = await dispatcherB.SharedSessions.SendAsync(threadId,
                "不要调用工具。停止原主题，最终只回复：" + marker, turnId, timeout.Token);
            Assert(steered.Succeeded, "steer after first client disposal");
            Assert(await completed.Task.WaitAsync(timeout.Token) == "completed", "same Turn completed");
            var result = await b.SendRequestAsync("thread/read", new { threadId, includeTurns = true }, TimeSpan.FromSeconds(20), timeout.Token);
            var turn = result.GetProperty("thread").GetProperty("turns").EnumerateArray().Single(t => t.GetProperty("id").GetString() == turnId);
            Assert(turn.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("type").GetString() == "agentMessage" &&
                i.TryGetProperty("text", out var text) && text.GetString()!.Contains(marker, StringComparison.Ordinal)), "steer semantic marker");
            var identity = await SharedServiceIdentity.ReadAndVerifyAsync(manifest, new Uri(endpoint), timeout.Token);
            Assert(identity.ServiceInstanceId == b.ServiceInstanceId, "service survived client disposal");
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
            {
                passed = true, threadId, turnId, sameService = true, secondClientStreaming = true,
                firstClientDisposed = true, semanticSteer = true, completed = true, desktopUiVerified = false,
                automaticApproval = false, serviceInstanceId = b.ServiceInstanceId,
            }, RelayJson.Options), timeout.Token);
            Console.WriteLine("Shared product evidence: " + Path.Combine(root, "result.json"));
        }
        finally
        {
            if (threadId is not null && turnId is not null && !completed.Task.IsCompleted)
            {
                try { await b.SendRequestAsync("turn/interrupt", new { threadId, turnId }, TimeSpan.FromSeconds(15), CancellationToken.None); }
                catch { /* Never stop the shared process or touch another Thread to clean up this test. */ }
            }
        }
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
