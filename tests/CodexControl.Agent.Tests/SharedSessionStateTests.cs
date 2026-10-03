using System.Text.Json;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Diagnostics;
using CodexControl.Agent.Relay;
using CodexControl.Agent.State;

namespace CodexControl.Agent.Tests;

internal static class SharedSessionStateTests
{
    public static Task StateAsync()
    {
        foreach (var idleFirst in new[] { true, false })
        {
            var state = new CodexStateManager();
            state.MarkIdle();
            Apply(state, """{"method":"thread/status/changed","params":{"threadId":"a","status":{"type":"active","activeFlags":["waitingOnApproval"]}}}""");
            Check(state.Snapshot.Threads.Single().ActiveTurnId is null &&
                state.Snapshot.Status == CodexActivityStatus.WaitingApproval && state.Snapshot.Threads.Single().RequiresRefresh,
                "active before turn start must remain visible and require recovery");
            Apply(state, Start("a", "a1"));
            Check(state.Snapshot.Status == CodexActivityStatus.WaitingApproval, "turn start must preserve authoritative waiting flags");
            Apply(state, """{"method":"item/started","params":{"threadId":"a","turnId":"a1","item":{"id":"cmd","type":"commandExecution","command":"echo test"}}}""");
            Check(state.Snapshot.Status == CodexActivityStatus.WaitingApproval, "item activity must not erase pending approval");
            var idle = """{"method":"thread/status/changed","params":{"threadId":"a","status":{"type":"idle"}}}""";
            var completed = End("a", "a1", "interrupted");
            Apply(state, idleFirst ? idle : completed);
            Apply(state, idleFirst ? completed : idle);
            var ended = state.Snapshot.Threads.Single();
            Check(ended.ThreadState == "idle" && ended.LastTurnStatus == "interrupted" && ended.ActiveTurnId is null,
                "idle/completion order must preserve the same terminal result");
            Check(state.Snapshot.Status == CodexActivityStatus.Interrupted, "idle must not erase interrupted state");
            var before = state.Snapshot.Revision;
            Apply(state, """{"method":"item/completed","params":{"threadId":"a","turnId":"a1","item":{"id":"old","type":"agentMessage","text":"late"}}}""");
            Check(state.Snapshot.Revision == before && state.Snapshot.LastAgentMessage is null, "old item cannot resurrect terminal turn");
        }

        var parallel = new CodexStateManager { AwaitServerResolution = true };
        parallel.MarkIdle();
        Apply(parallel, """{"method":"thread/started","params":{"thread":{"id":"a","cwd":"D:\\project-a"}}}""");
        Apply(parallel, Start("a", "a1"));
        Apply(parallel, Start("b", "b1"));
        Check(parallel.Snapshot.ActiveTurns.Single(value => value.ThreadId == "b").CurrentProject is null,
            "project must not fall back to another thread");
        var revision = parallel.Snapshot.Revision;
        Apply(parallel, """{"method":"item/started","params":{"item":{"type":"commandExecution","command":"echo unowned"}}}""");
        Check(parallel.Snapshot.Revision == revision, "missing IDs must not mutate focused thread");
        Apply(parallel, """{"method":"item/commandExecution/requestApproval","id":7,"params":{"threadId":"a","turnId":"a1","itemId":"cmd","availableDecisions":["cancel"]}}""");
        Apply(parallel, """{"method":"item/started","params":{"threadId":"b","turnId":"b1","item":{"type":"commandExecution","command":"echo other"}}}""");
        Check(parallel.Snapshot.Status == CodexActivityStatus.WaitingApproval &&
            parallel.Snapshot.ActiveTurns.Single(value => value.ThreadId == "b").Status == CodexActivityStatus.RunningCommand,
            "thread B activity cannot clear thread A approval");
        using (var sent = JsonDocument.Parse("""{"id":7,"result":{"decision":"cancel"}}"""))
            parallel.ApplyClientMessage(sent.RootElement);
        Check(parallel.Snapshot.PendingApprovalCount == 1, "shared response transmission is not resolution");
        Apply(parallel, """{"method":"serverRequest/resolved","params":{"requestId":7}}""");
        Check(parallel.Snapshot.PendingApprovalCount == 0, "server resolution clears matching approval");
        Apply(parallel, Start("a", "a2"));
        Apply(parallel, End("a", "a1", "completed"));
        Check(parallel.Snapshot.ActiveTurns.Single(value => value.ThreadId == "a").TurnId == "a2",
            "delayed old completion cannot clear a newer turn");
        Apply(parallel, End("a", "a2", "completed"));
        Check(parallel.Snapshot.ActiveTurns.Single().ThreadId == "b", "completion must not clear another thread");
        parallel.MarkOffline("test disconnect");
        Check(parallel.Snapshot.ConnectionState == "offline" && parallel.Snapshot.ActiveTurns.Single().TurnId == "b1" &&
            parallel.Snapshot.Threads.Single(value => value.ThreadId == "b").RequiresRefresh,
            "disconnect must retain running task with stale freshness");

        var restored = new CodexStateManager();
        using var context = JsonDocument.Parse("""{"thread":{"id":"restore","cwd":"D:\\restored","status":{"type":"active","activeFlags":["waitingOnUserInput"]},"turns":[{"id":"r1","status":"inProgress","startedAt":100}]}}""");
        Check(restored.ApplyThreadContext(context.RootElement), "public context must restore running turn");
        Check(restored.Snapshot.ActiveTurnId == "r1" && restored.Snapshot.Status == CodexActivityStatus.WaitingUserInput,
            "recovered context must expose active turn and wait flags");
        var capturedRevision = restored.Snapshot.Revision;
        Apply(restored, End("restore", "r1", "completed"));
        Check(!restored.ApplyThreadContext(context.RootElement, capturedRevision), "snapshot racing live events must be rejected");

        var transition = new CodexStateManager();
        Apply(transition, Start("transition", "old"));
        Apply(transition, """{"method":"thread/status/changed","params":{"threadId":"transition","status":{"type":"idle"}}}""");
        Apply(transition, """{"method":"thread/status/changed","params":{"threadId":"transition","status":{"type":"active","activeFlags":[]}}}""");
        Apply(transition, """{"method":"item/started","params":{"threadId":"transition","turnId":"old","item":{"id":"late","type":"commandExecution","command":"echo stale"}}}""");
        Apply(transition, End("transition", "old", "completed"));
        Check(transition.Snapshot.Threads.Single().ThreadState == "active" && transition.Snapshot.ActiveTurnId is null &&
            transition.Snapshot.RunningCommand is null && transition.Snapshot.Threads.Single().RequiresRefresh,
            "idle/new-active gap must not bind an old item or end the new unknown turn");
        Apply(transition, Start("transition", "new"));
        Check(transition.Snapshot.ActiveTurnId == "new", "newly identified turn must replace pending recovery");
        return Task.CompletedTask;
    }

    public static async Task ApprovalAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "codex-control-shared-approval-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var log = new AgentLog(path);
            var sent = new List<string>();
            var coordinator = new ApprovalCoordinator((raw, _) => { sent.Add(raw); return ValueTask.CompletedTask; }, log,
                awaitServerResolution: true);
            var resolved = 0;
            var resolving = 0;
            coordinator.ApprovalResolved += _ => resolved++;
            coordinator.ApprovalResolving += _ => resolving++;
            Observe(coordinator, ApprovalRequest("a", "a1", 7));
            var pending = coordinator.PendingApprovals.Single();
            using var cancel = JsonDocument.Parse("\"cancel\"");
            var result = await coordinator.ResolveRemoteAsync(pending.ApprovalId, cancel.RootElement, "web", CancellationToken.None);
            Check(result.Succeeded && sent.Count == 1 && resolved == 0 && resolving == 1 &&
                coordinator.PendingApprovals.Single().IsResolving, "write must leave approval resolving until server evidence");
            var duplicate = await coordinator.ResolveRemoteAsync(pending.ApprovalId, cancel.RootElement, "other", CancellationToken.None);
            Check(!duplicate.Succeeded && sent.Count == 1, "concurrent local response must be suppressed");
            Observe(coordinator, """{"method":"turn/completed","params":{}}""");
            Observe(coordinator, End("b", "b1", "completed"));
            Check(resolved == 0, "unrelated or unidentified lifecycle cannot resolve approval");
            Observe(coordinator, """{"method":"serverRequest/resolved","params":{"requestId":7}}""");
            Observe(coordinator, """{"method":"serverRequest/resolved","params":{"requestId":7}}""");
            Check(resolved == 1 && coordinator.PendingApprovals.Count == 0, "one server resolution must settle exactly once");

            Observe(coordinator, ApprovalRequest("a", "a2", 8));
            var oldId = coordinator.PendingApprovals.Single().ApprovalId;
            coordinator.ResetConnection();
            var stale = await coordinator.ResolveRemoteAsync(oldId, cancel.RootElement, "web", CancellationToken.None);
            Check(!stale.Succeeded && resolved == 1 && coordinator.PendingApprovals.Count == 0,
                "disconnect invalidates old request IDs without inventing server resolution");
            coordinator.BeginConnection();
            Observe(coordinator, ApprovalRequest("a", "a2", 8));
            Check(coordinator.PendingApprovals.Single().ApprovalId != oldId, "replayed request must get fresh connection mapping");
            Observe(coordinator, End("a", "a2", "interrupted"));
            Check(resolved == 2, "matching authoritative terminal lifecycle must clear pending approval");
        }
        finally
        {
            if (Path.GetFullPath(path).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(path, recursive: true);
        }
    }

    public static Task NormalizerAsync()
    {
        var state = new CodexStateManager();
        Apply(state, Start("focused", "focused-turn"));
        using var unknown = JsonDocument.Parse("""{"method":"item/agentMessage/delta","params":{"delta":"unowned"}}""");
        var unowned = DomainEventNormalizer.Normalize(unknown.RootElement, state.Snapshot)!;
        Check(unowned.Kind == "UnattributedEvent" && unowned.ThreadId is null && unowned.TurnId is null &&
            !unowned.Data.TryGetProperty("delta", out _), "missing identity must emit only recovery metadata");
        using var unownedTurn = JsonDocument.Parse("""{"method":"item/agentMessage/delta","params":{"threadId":"focused","delta":"unknown-turn"}}""");
        Check(DomainEventNormalizer.Normalize(unownedTurn.RootElement, state.Snapshot)!.TurnId is null,
            "thread alone cannot identify a concurrent delayed turn item");
        var text = new string('x', 40_000);
        using var completed = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            method = "item/completed", @params = new { threadId = "a", turnId = "a1", item = new { id = "msg", type = "agentMessage", text } },
        }));
        var complete = DomainEventNormalizer.Normalize(completed.RootElement, state.Snapshot)!;
        Check(complete.Data.GetProperty("text").GetString() == text && !complete.Data.GetProperty("truncated").GetBoolean(),
            "normal completed body must not silently truncate at legacy 20k limit");
        using var longDelta = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            method = "item/agentMessage/delta", @params = new { threadId = "a", turnId = "a1", itemId = "msg", delta = new string('x', 140_000) },
        }));
        var limited = DomainEventNormalizer.Normalize(longDelta.RootElement, state.Snapshot)!;
        Check(limited.Data.GetProperty("truncated").GetBoolean() && limited.Data.GetProperty("originalLength").GetInt32() == 140_000 &&
            limited.Data.GetProperty("resyncRequired").GetBoolean(), "over-frame content must explicitly mark incompleteness");
        using var command = JsonDocument.Parse("""{"method":"item/completed","params":{"threadId":"a","turnId":"a1","item":{"id":"cmd","type":"commandExecution","command":"echo done","aggregatedOutput":"done","exitCode":0,"status":"completed"}}}""");
        var commandEvent = DomainEventNormalizer.Normalize(command.RootElement, state.Snapshot)!;
        Check(commandEvent.Data.GetProperty("output").GetString() == "done" && commandEvent.Data.GetProperty("exitCode").GetInt64() == 0,
            "command completion must include public output and exit status");
        foreach (var knownType in new[] { "agentMessage", "fileChange", "reasoning" })
        {
            using var known = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                method = "item/started", @params = new { threadId = "a", turnId = "a1", item = new { id = "known", type = knownType } },
            }));
            Check(DomainEventNormalizer.Normalize(known.RootElement, state.Snapshot) is null,
                "known item starts must not be labeled unsupported or replace the eventual body");
        }
        using var unsupported = JsonDocument.Parse("""{"method":"item/started","params":{"threadId":"a","turnId":"a1","item":{"id":"new-type","type":"futureToolType"}}}""");
        Check(DomainEventNormalizer.Normalize(unsupported.RootElement, state.Snapshot)!.Kind == "UnsupportedItem",
            "genuinely unknown item must remain explicitly unsupported");
        return Task.CompletedTask;
    }

    private static string Start(string thread, string turn) => JsonSerializer.Serialize(new
    { method = "turn/started", @params = new { threadId = thread, turn = new { id = turn } } });
    private static string End(string thread, string turn, string status) => JsonSerializer.Serialize(new
    { method = "turn/completed", @params = new { threadId = thread, turn = new { id = turn, status } } });
    private static string ApprovalRequest(string thread, string turn, int id) => JsonSerializer.Serialize(new
    { method = "item/commandExecution/requestApproval", id, @params = new { threadId = thread, turnId = turn, itemId = "cmd", availableDecisions = new[] { "cancel" } } });
    private static void Apply(CodexStateManager state, string json)
    { using var document = JsonDocument.Parse(json); state.ApplyServerMessage(document.RootElement); }
    private static void Observe(ApprovalCoordinator coordinator, string json)
    { using var document = JsonDocument.Parse(json); coordinator.ObserveServerMessage(document.RootElement); }
    private static void Check(bool condition, string description)
    { if (!condition) throw new InvalidOperationException(description); }
}
