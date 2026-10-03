using System.Collections.Concurrent;
using System.Text.Json;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Diagnostics;
using CodexControl.Agent.State;
using CodexControl.Protocol;

namespace CodexControl.Agent.Control;

/// <summary>Joins a shared Thread without overriding its model, cwd, or permissions.</summary>
public sealed class SharedSessionOperations(AppServerBridge bridge, CodexStateManager state, SharedSessionRuntimeContext? runtimeContext = null)
{
    private readonly SharedSessionRuntimeContext _runtime = runtimeContext ?? new();
    private readonly ConcurrentDictionary<string, string> _workspaces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _observed = new(StringComparer.Ordinal);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    internal string? ObserveCreatedThread(JsonElement context, string threadId, string workspace)
    {
        RequireShared(threadId);
        if (!context.TryGetProperty("thread", out var thread) ||
            !thread.TryGetProperty("id", out var id) || id.GetString() != threadId)
            return "THREAD_ID_MISMATCH";
        var reason = ThreadWorkspacePolicy.RejectionReason(context, workspace, threadId);
        if (reason is not null) return reason;
        if (!ThreadWorkspacePolicy.SamePath(thread.GetProperty("cwd").GetString(), workspace))
            return "PROJECT_SCOPE_CHANGED";
        if (!bridge.AuthorizedProjectRoots.Any(root => ThreadWorkspacePolicy.IsWithinDirectory(workspace, root)))
            return "PROJECT_NOT_AUTHORIZED";
        var rememberedWorkspace = _runtime.ObservedWorkspace(bridge.ServiceInstanceId, threadId);
        var authorizedWorkspace = _workspaces.GetOrAdd(threadId, rememberedWorkspace ?? workspace);
        if (!ThreadWorkspacePolicy.SamePath(workspace, authorizedWorkspace)) return "PROJECT_SCOPE_CHANGED";
        // thread/start already subscribes its creator and returns the effective policy.
        // Before the first Turn, the new Thread may have no persisted rollout to resume.
        _observed[threadId] = bridge.ConnectionEpoch;
        _runtime.RememberObservation(bridge.ServiceInstanceId, threadId, authorizedWorkspace);
        return null;
    }

    public async Task<(JsonElement Context, string? PolicyReason)> JoinAsync(string threadId, CancellationToken token, bool allowJoin = true)
    {
        RequireShared(threadId);
        var rememberedWorkspace = _runtime.ObservedWorkspace(bridge.ServiceInstanceId, threadId);
        if (!allowJoin && rememberedWorkspace is null &&
            (!_observed.TryGetValue(threadId, out var epoch) || epoch != bridge.ConnectionEpoch))
            throw new AgentException("STEER_PERMISSION_REQUIRED", "First subscription requires steer permission.");
        var metadata = await bridge.SendRequestAsync("thread/read", new { threadId, includeTurns = false }, Timeout, token);
        var thread = metadata.GetProperty("thread");
        if (thread.GetProperty("id").GetString() != threadId) throw new AgentException("THREAD_ID_MISMATCH", "Thread identity changed.");
        var cwd = thread.GetProperty("cwd").GetString();
        if (cwd is null || !ThreadWorkspacePolicy.SafeDirectory(cwd))
            throw new AgentException("PROJECT_SCOPE_INVALID", "Thread workspace is unavailable.");
        var authorizedWorkspace = _workspaces.GetOrAdd(threadId, rememberedWorkspace ?? cwd);
        if (!ThreadWorkspacePolicy.SamePath(cwd, authorizedWorkspace))
            throw new AgentException("PROJECT_SCOPE_CHANGED", "Thread workspace changed; select the project again.");

        var revision = state.Snapshot.Revision;
        var context = await bridge.SendRequestAsync("thread/resume", new { threadId, excludeTurns = true }, Timeout, token);
        var turns = await bridge.SendRequestAsync("thread/turns/list",
            new { threadId, limit = 20, sortDirection = "desc", itemsView = "summary" }, Timeout, token);
        // Resume with excludeTurns carries no active-turn metadata. Inject only our explicit summary read.
        var restored = JsonSerializer.SerializeToElement(new
        {
            thread = new { id = threadId, cwd, status = context.GetProperty("thread").GetProperty("status"),
                turns = turns.GetProperty("data").EnumerateArray().Reverse().ToArray() },
        }, RelayJson.Options);
        if (!state.ApplyThreadContext(restored, revision))
        {
            // One bounded recovery on a racing target-thread notification, never history polling.
            revision = state.Snapshot.Revision;
            var current = await bridge.SendRequestAsync("thread/read", new { threadId, includeTurns = true }, Timeout, token);
            state.ApplyThreadContext(current, revision);
        }
        _observed[threadId] = bridge.ConnectionEpoch;
        _runtime.RememberObservation(bridge.ServiceInstanceId, threadId, authorizedWorkspace);
        var authorized = bridge.AuthorizedProjectRoots.Any(root => ThreadWorkspacePolicy.IsWithinDirectory(authorizedWorkspace, root));
        return (context, authorized ? ThreadWorkspacePolicy.RejectionReason(context, authorizedWorkspace, threadId,
            allowDesktopTemporaryDirectories: bridge.AllowStandardTemporaryDirectories) : "PROJECT_NOT_AUTHORIZED");
    }

    public async Task<ControlDispatchResult> SendAsync(string threadId, string text, string? expectedTurnId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 20_000)
            return new(false, "INVALID_CONTROL_PAYLOAD", "任务内容不能为空且不能超过 20000 个字符。", null);
        RequireShared(threadId);
        var gate = _gates.GetOrAdd(threadId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            var (context, reason) = await JoinAsync(threadId, token);
            if (reason is not null) return new(false, reason, "当前会话权限需要核实；未修改其配置。", null);
            var status = context.GetProperty("thread").GetProperty("status").GetProperty("type").GetString();
            JsonElement result;
            string? turnId;
            if (expectedTurnId is not null)
            {
                if (!Guid.TryParseExact(expectedTurnId, "D", out _) || status != "active")
                    return new(false, "TURN_MISMATCH", "目标 Turn 已变化，请同步状态后发送。", null);
                result = await bridge.SendRequestAsync("turn/steer", new
                { threadId, expectedTurnId, input = new[] { new { type = "text", text } } }, Timeout, token);
                turnId = result.GetProperty("turnId").GetString();
            }
            else
            {
                if (status != "idle") return new(false, "TURN_ALREADY_ACTIVE", "会话正在运行，请向当前 Turn 发送 Steer。", null);
                result = await bridge.SendRequestAsync("turn/start", new
                { threadId, input = new[] { new { type = "text", text } } }, Timeout, token);
                turnId = result.GetProperty("turn").GetProperty("id").GetString();
            }
            return new(true, null, null, JsonSerializer.SerializeToElement(new CodexThreadActionResultPayload(threadId, turnId!), RelayJson.Options));
        }
        catch (AppServerRpcException) { return new(false, "SHARED_CONTROL_REJECTED", "共享服务拒绝了请求；未自动重发。", null); }
        catch (TimeoutException) { return new(false, "CONTROL_OUTCOME_UNKNOWN", "提交结果未知；请等待同步，不要自动重发。", null); }
        catch (AgentException e) { return new(false, e.Code, "共享会话连接或权限不可用。", null); }
        finally { gate.Release(); }
    }

    private void RequireShared(string threadId)
    {
        if (!bridge.IsSharedSession) throw new AgentException("SHARED_SESSION_REQUIRED", "Connect to the shared service first.");
        if (!Guid.TryParseExact(threadId, "D", out _)) throw new AgentException("INVALID_THREAD_ID", "Invalid Thread ID.");
    }
}
