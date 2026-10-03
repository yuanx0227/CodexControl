using System.Text.Json;
using CodexControl.Agent.Codex;

namespace CodexControl.Agent.State;

/// <summary>Single writer for per-thread execution state. Connection loss never completes a turn.</summary>
public sealed class CodexStateManager
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ThreadEntry> _threads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingApprovalContext> _pendingApprovals = new(StringComparer.Ordinal);
    // Inactive identities prevent delayed items from reviving an old turn. They do
    // not imply a successful result; LastTurnStatus only comes from terminal data.
    private readonly HashSet<(string Thread, string Turn)> _inactiveTurns = [];
    private readonly Queue<(string Thread, string Turn)> _inactiveOrder = new();
    private CodexStateSnapshot _snapshot = CodexStateSnapshot.Initial;

    public CodexStateSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public bool AwaitServerResolution { get; set; }
    public event Action<CodexStateSnapshot>? SnapshotChanged;

    public void MarkStarting() => MarkConnectionState("connecting");

    public void MarkIdle()
    {
        lock (_gate)
        {
            Publish(_snapshot with { ConnectionState = "online", LastError = null },
                fallback: CodexActivityStatus.Idle);
        }
    }

    public void MarkOffline(string? reason = null) => SetConnectionState("offline", reason);
    public void MarkFailed(string reason) => SetConnectionState("offline", reason);
    public void MarkConnectionState(string state) => SetConnectionState(state, null);

    private void SetConnectionState(string state, string? reason)
    {
        if (state is not ("connecting" or "online" or "reconnecting" or "offline"))
            throw new ArgumentOutOfRangeException(nameof(state));
        lock (_gate)
        {
            if (state != "online")
                foreach (var entry in _threads.Values) entry.RequiresRefresh = true;
            Publish(_snapshot with { ConnectionState = state, LastError = reason }, fallback:
                state is "connecting" or "reconnecting" ? CodexActivityStatus.Starting :
                state == "online" ? CodexActivityStatus.Idle : CodexActivityStatus.Offline);
        }
    }

    /// <summary>Request IDs are scoped to the old connection; server flags remain until recovery.</summary>
    public void ResetConnectionApprovals()
    {
        lock (_gate)
        {
            _pendingApprovals.Clear();
            foreach (var entry in _threads.Values) entry.RequiresRefresh = true;
            Publish(_snapshot);
        }
    }

    public void SetRelayConnected(bool connected)
    {
        lock (_gate) Publish(_snapshot with { RelayConnected = connected });
    }

    public void SetPairedControllerCount(int count)
    {
        lock (_gate) Publish(_snapshot with { PairedControllerCount = Math.Max(0, count) });
    }

    /// <summary>Accepts public thread/read or thread/resume result. Never changes server settings.</summary>
    public bool ApplyThreadContext(JsonElement data, long? expectedRevision = null)
    {
        if (data.ValueKind != JsonValueKind.Object) return false;
        if (data.TryGetProperty("result", out var result)) data = result;
        if (data.ValueKind != JsonValueKind.Object) return false;
        if (data.TryGetProperty("thread", out var thread)) data = thread;
        var id = FindString(data, "id");
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_gate)
        {
            // A caller can reject a snapshot that raced with a newer notification.
            var entry = GetThread(id);
            if (expectedRevision is not null && entry.Revision > expectedRevision) return false;
            entry.CurrentProject = FindString(data, "cwd") ?? entry.CurrentProject;
            JsonElement? latest = null;
            JsonElement? running = null;
            if (TryFind(data, out var turns, "turns") && turns.ValueKind == JsonValueKind.Array)
            {
                foreach (var turn in turns.EnumerateArray())
                {
                    latest = turn;
                    if (FindString(turn, "status") == "inProgress") running = turn;
                }
            }
            var type = FindString(data, "status", "type") ?? FindString(data, "status");
            if (running is { } active && FindString(active, "id") is { } turnId &&
                !_inactiveTurns.Contains((id, turnId)))
            {
                StartTurn(entry, turnId, ReadTimestamp(active, "startedAt"));
            }
            else if (latest is { } last && FindString(last, "id") is { } lastId &&
                     FindString(last, "status") is "completed" or "interrupted" or "failed")
            {
                // Do not overwrite a different running turn from newer live notifications.
                if (entry.TurnId is null || entry.ThreadState != "active" || entry.TurnId == lastId)
                    FinishTurn(entry, lastId, FindString(last, "status")!);
            }
            if (type is not null) SetThreadStatus(entry, type, ReadFlags(data, "status", "activeFlags"));
            else if (entry.ThreadState == "unknown" && running is null) entry.ThreadState = "idle";
            entry.RequiresRefresh = entry.ThreadState == "active" && entry.TurnId is null;
            Touch(entry);
            Publish(_snapshot, entry);
            return true;
        }
    }

    public void ApplyServerMessage(JsonElement message)
    {
        if (!JsonRpcProtocol.TryGetMethod(message, out var method)) return;
        if (method is not ("serverRequest/resolved" or "thread/started" or "thread/status/changed" or
            "turn/started" or "turn/completed" or "item/started" or "item/completed" or "item/agentMessage/delta") &&
            !method.EndsWith("/requestApproval", StringComparison.Ordinal)) return;
        lock (_gate)
        {
            if (method == "serverRequest/resolved")
            {
                if (TryFind(message, out var requestId, "params", "requestId"))
                    ResolveApproval(JsonRpcProtocol.GetIdKey(requestId));
                return;
            }
            var threadId = FindThreadId(message);
            var turnId = FindTurnId(message);
            // A known turn can identify its thread, but current UI focus cannot.
            if (threadId is null && turnId is not null)
                threadId = _threads.Values.FirstOrDefault(value => value.TurnId == turnId)?.Id;
            if (threadId is null) return;
            var entry = GetThread(threadId);
            switch (method)
            {
                case "thread/started":
                    entry.CurrentProject = FindString(message, "params", "thread", "cwd") ?? entry.CurrentProject;
                    if (TryFind(message, out var threadStatus, "params", "thread", "status"))
                        SetThreadStatus(entry, FindString(threadStatus, "type") ?? "unknown", ReadFlags(threadStatus, "activeFlags"));
                    else if (entry.ThreadState == "unknown") entry.ThreadState = "idle";
                    break;
                case "thread/status/changed":
                    SetThreadStatus(entry, FindString(message, "params", "status", "type") ?? "unknown",
                        ReadFlags(message, "params", "status", "activeFlags"));
                    break;
                case "turn/started":
                    if (turnId is null || _inactiveTurns.Contains((threadId, turnId))) return;
                    StartTurn(entry, turnId, TryFind(message, out var turn, "params", "turn")
                        ? ReadTimestamp(turn, "startedAt") : null);
                    break;
                case "turn/completed":
                    if (turnId is null || (entry.TurnId is not null && entry.TurnId != turnId)) return;
                    if (entry.ThreadState == "active" && entry.TurnId is null && _inactiveTurns.Contains((threadId, turnId)))
                    {
                        // A new active turn is still being identified. The old terminal
                        // result is useful, but cannot end the newly reported activity.
                        entry.LastTurnId = turnId;
                        entry.LastTurnStatus = TerminalStatus(FindString(message, "params", "turn", "status"));
                        break;
                    }
                    FinishTurn(entry, turnId, FindString(message, "params", "turn", "status") ?? "completed");
                    break;
                case "item/started":
                case "item/completed":
                case "item/agentMessage/delta":
                    if (!MatchesActiveTurn(entry, turnId)) return;
                    ApplyItem(entry, message, method);
                    break;
                default:
                    if (method.EndsWith("/requestApproval", StringComparison.Ordinal) &&
                        JsonRpcProtocol.TryGetId(message, out var approvalId))
                    {
                        if (turnId is null || _inactiveTurns.Contains((threadId, turnId)) ||
                            (entry.TurnId is not null && entry.TurnId != turnId)) return;
                        if (entry.TurnId is null) StartTurn(entry, turnId, null);
                        _pendingApprovals[JsonRpcProtocol.GetIdKey(approvalId)] = new(threadId, turnId);
                        entry.WaitingOnApproval = true;
                    }
                    else return;
                    break;
            }
            Touch(entry);
            Publish(_snapshot, entry);
        }
    }

    public void ApplyClientMessage(JsonElement message)
    {
        if (AwaitServerResolution) return;
        if (JsonRpcProtocol.IsResponse(message) && JsonRpcProtocol.TryGetId(message, out var id))
        {
            lock (_gate) ResolveApproval(JsonRpcProtocol.GetIdKey(id));
        }
    }

    private void ResolveApproval(string requestId)
    {
        if (!_pendingApprovals.Remove(requestId, out var context)) return;
        if (_threads.TryGetValue(context.ThreadId, out var entry) && entry.TurnId == context.TurnId)
        {
            entry.WaitingOnApproval = PendingApprovalCount(entry) > 0;
            Touch(entry);
            Publish(_snapshot, entry);
        }
        else Publish(_snapshot);
    }

    private void SetThreadStatus(ThreadEntry entry, string type, IReadOnlyList<string> flags)
    {
        entry.ThreadState = type is "active" or "idle" or "notLoaded" or "systemError" ? type : "unknown";
        if (type == "active")
        {
            if (entry.TurnId is not null && _inactiveTurns.Contains((entry.Id, entry.TurnId)))
                entry.TurnId = null;
            entry.WaitingOnApproval = flags.Contains("waitingOnApproval") || PendingApprovalCount(entry) > 0;
            entry.WaitingOnUserInput = flags.Contains("waitingOnUserInput");
            entry.RequiresRefresh = entry.TurnId is null;
        }
        else
        {
            if (type == "idle" && entry.TurnId is not null) RememberInactive(entry.Id, entry.TurnId);
            entry.WaitingOnApproval = false;
            entry.WaitingOnUserInput = false;
            entry.RunningCommand = null;
            RemoveApprovals(entry.Id, null);
            // Keep the last turn identity until its completion notification arrives.
            // idle says nothing about whether that turn completed, failed or was interrupted.
            entry.RequiresRefresh = type is "notLoaded" or "systemError" or "unknown";
        }
    }

    private void StartTurn(ThreadEntry entry, string turnId, DateTimeOffset? startedAt)
    {
        if (entry.TurnId != turnId)
        {
            var retainFlags = entry.ThreadState == "active" && entry.TurnId is null;
            RemoveApprovals(entry.Id, null);
            entry.StartedAt = startedAt ?? DateTimeOffset.UtcNow;
            entry.ChangedFiles = [];
            entry.LastAgentMessage = null;
            entry.RunningCommand = null;
            entry.Activity = CodexActivityStatus.Thinking;
            entry.CurrentActivity = "Codex is thinking";
            if (!retainFlags)
            {
                entry.WaitingOnApproval = false;
                entry.WaitingOnUserInput = false;
            }
        }
        entry.TurnId = turnId;
        entry.ThreadState = "active";
        entry.RequiresRefresh = false;
        entry.LastError = null;
    }

    private void FinishTurn(ThreadEntry entry, string turnId, string rawStatus)
    {
        var status = TerminalStatus(rawStatus);
        entry.TurnId = turnId;
        entry.LastTurnId = turnId;
        entry.LastTurnStatus = status;
        entry.ThreadState = "idle";
        entry.WaitingOnApproval = false;
        entry.WaitingOnUserInput = false;
        entry.RunningCommand = null;
        entry.CurrentActivity = null;
        entry.RequiresRefresh = false;
        entry.LastError = status == "failed" ? "Turn failed" : null;
        RemoveApprovals(entry.Id, turnId);
        RememberInactive(entry.Id, turnId);
    }

    private bool MatchesActiveTurn(ThreadEntry entry, string? turnId)
    {
        if (entry.ThreadState != "active" || turnId is null || _inactiveTurns.Contains((entry.Id, turnId)))
            return false;
        if (entry.TurnId is null)
        {
            entry.TurnId = turnId;
            entry.RequiresRefresh = false;
        }
        return entry.TurnId == turnId;
    }

    private static void ApplyItem(ThreadEntry entry, JsonElement message, string method)
    {
        var itemType = FindString(message, "params", "item", "type");
        if (method == "item/started")
        {
            entry.RunningCommand = FindString(message, "params", "item", "command");
            entry.Activity = itemType switch
            {
                "commandExecution" when LooksLikeTestCommand(entry.RunningCommand) => CodexActivityStatus.RunningTests,
                "commandExecution" => CodexActivityStatus.RunningCommand,
                "fileChange" => CodexActivityStatus.Editing,
                "reasoning" or "agentMessage" => CodexActivityStatus.Thinking,
                _ => CodexActivityStatus.Reading,
            };
            entry.CurrentActivity = itemType;
        }
        else if (method == "item/completed")
        {
            entry.Activity = CodexActivityStatus.Thinking;
            entry.CurrentActivity = "Codex is thinking";
            entry.RunningCommand = null;
            if (itemType == "agentMessage") entry.LastAgentMessage = FindString(message, "params", "item", "text");
            if (itemType == "fileChange" && TryFind(message, out var changes, "params", "item", "changes") &&
                changes.ValueKind == JsonValueKind.Array)
                entry.ChangedFiles = changes.EnumerateArray().Select(change => FindString(change, "path"))
                    .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
        }
        else
        {
            entry.Activity = CodexActivityStatus.Thinking;
            entry.CurrentActivity = "Streaming response";
        }
    }

    private void RemoveApprovals(string threadId, string? turnId)
    {
        foreach (var key in _pendingApprovals.Where(pair => pair.Value.ThreadId == threadId &&
                     (turnId is null || pair.Value.TurnId == turnId)).Select(pair => pair.Key).ToArray())
            _pendingApprovals.Remove(key);
    }

    private int PendingApprovalCount(ThreadEntry entry) => _pendingApprovals.Values.Count(value =>
        value.ThreadId == entry.Id && (entry.TurnId is null || value.TurnId == entry.TurnId));

    private static string TerminalStatus(string? status) => status is "interrupted" or "failed" ? status : "completed";
    private void RememberInactive(string threadId, string turnId)
    {
        if (_inactiveTurns.Add((threadId, turnId))) _inactiveOrder.Enqueue((threadId, turnId));
        while (_inactiveOrder.Count > 4096) _inactiveTurns.Remove(_inactiveOrder.Dequeue());
    }

    private CodexActivityStatus Status(ThreadEntry entry)
    {
        if (entry.ThreadState == "active")
            return entry.WaitingOnApproval || PendingApprovalCount(entry) > 0 ? CodexActivityStatus.WaitingApproval :
                entry.WaitingOnUserInput ? CodexActivityStatus.WaitingUserInput : entry.Activity;
        if (entry.LastTurnStatus is not null && entry.LastTurnId == entry.TurnId)
            return entry.LastTurnStatus switch
            {
                "interrupted" => CodexActivityStatus.Interrupted,
                "failed" => CodexActivityStatus.Failed,
                _ => CodexActivityStatus.Completed,
            };
        return entry.ThreadState == "idle" ? CodexActivityStatus.Idle : CodexActivityStatus.Unknown;
    }

    private void Publish(CodexStateSnapshot basis, ThreadEntry? changed = null, CodexActivityStatus? fallback = null)
    {
        var active = _threads.Values.Where(value => value.ThreadState == "active")
            .OrderByDescending(value => value.WaitingOnApproval || PendingApprovalCount(value) > 0)
            .ThenByDescending(value => value.WaitingOnUserInput)
            .ThenByDescending(value => value.LastActivityAt).ToArray();
        var focus = active.FirstOrDefault() ?? changed ??
            (_snapshot.ActiveThreadId is { } id ? _threads.GetValueOrDefault(id) : null);
        var published = basis with
        {
            Revision = _snapshot.Revision + 1,
            LastActivityAt = DateTimeOffset.UtcNow,
            Status = focus is null ? fallback ?? basis.Status : Status(focus),
            ActiveThreadId = focus?.Id,
            ActiveTurnId = focus?.ThreadState == "active" ? focus.TurnId : null,
            StartedAt = focus?.StartedAt,
            CurrentProject = focus?.CurrentProject,
            CurrentActivity = focus is null ? null : Status(focus) switch
            {
                CodexActivityStatus.WaitingApproval => "Waiting for approval",
                CodexActivityStatus.WaitingUserInput => "Waiting for user input",
                _ => focus.ThreadState == "active" ? focus.CurrentActivity : null,
            },
            RunningCommand = focus?.ThreadState == "active" ? focus.RunningCommand : null,
            ChangedFiles = focus?.ChangedFiles ?? [],
            LastAgentMessage = focus?.LastAgentMessage,
            LastError = basis.ConnectionState == "online" ? focus?.LastError : basis.LastError,
            PendingApprovalCount = _pendingApprovals.Count,
            ActiveTurns = active.Where(value => value.TurnId is not null).Select(value => new CodexActiveTurnSnapshot(
                value.Id, value.TurnId!, Status(value), value.StartedAt, value.LastActivityAt, value.CurrentProject,
                value.CurrentActivity, value.RunningCommand, value.ChangedFiles, PendingApprovalCount(value),
                value.LastAgentMessage, value.LastError)).ToArray(),
            Threads = _threads.Values.Select(value => new CodexThreadStateSnapshot(
                value.Id, value.ThreadState, value.ThreadState == "active" ? value.TurnId : null,
                value.LastTurnId, value.LastTurnStatus, Status(value), value.WaitingOnApproval,
                value.WaitingOnUserInput, value.RequiresRefresh, value.LastActivityAt, value.CurrentProject)).ToArray(),
        };
        Volatile.Write(ref _snapshot, published);
        if (SnapshotChanged is not { } handlers) return;
        foreach (Action<CodexStateSnapshot> handler in handlers.GetInvocationList())
        {
            try { handler(published); }
            catch { /* Observers cannot interrupt protocol processing. */ }
        }
    }

    private ThreadEntry GetThread(string id)
    {
        if (!_threads.TryGetValue(id, out var entry)) _threads[id] = entry = new ThreadEntry(id);
        return entry;
    }
    private void Touch(ThreadEntry entry)
    {
        entry.LastActivityAt = DateTimeOffset.UtcNow;
        entry.Revision = _snapshot.Revision + 1;
    }
    private static bool LooksLikeTestCommand(string? command) =>
        command?.Contains(" test", StringComparison.OrdinalIgnoreCase) == true ||
        command?.StartsWith("test", StringComparison.OrdinalIgnoreCase) == true;
    private static string? FindThreadId(JsonElement message) =>
        FindString(message, "params", "threadId") ?? FindString(message, "params", "thread", "id") ??
        FindString(message, "params", "turn", "threadId") ?? FindString(message, "params", "item", "threadId");
    private static string? FindTurnId(JsonElement message) =>
        FindString(message, "params", "turn", "id") ?? FindString(message, "params", "turnId") ??
        FindString(message, "params", "item", "turnId");
    private static string? FindString(JsonElement root, params string[] path) =>
        TryFind(root, out var value, path) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static DateTimeOffset? ReadTimestamp(JsonElement root, string name) =>
        TryFind(root, out var value, name) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var seconds) && seconds is >= 0 and <= 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    private static IReadOnlyList<string> ReadFlags(JsonElement root, params string[] path) =>
        TryFind(root, out var value, path) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray() : [];
    private static bool TryFind(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var segment in path)
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            { value = default; return false; }
        return true;
    }

    private sealed record PendingApprovalContext(string ThreadId, string TurnId);
    private sealed class ThreadEntry(string id)
    {
        public string Id { get; } = id;
        public long Revision { get; set; }
        public string ThreadState { get; set; } = "unknown";
        public string? TurnId { get; set; }
        public string? LastTurnId { get; set; }
        public string? LastTurnStatus { get; set; }
        public CodexActivityStatus Activity { get; set; } = CodexActivityStatus.Thinking;
        public bool WaitingOnApproval { get; set; }
        public bool WaitingOnUserInput { get; set; }
        public bool RequiresRefresh { get; set; }
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
        public string? CurrentProject { get; set; }
        public string? CurrentActivity { get; set; } = "Synchronizing active turn";
        public string? RunningCommand { get; set; }
        public IReadOnlyList<string> ChangedFiles { get; set; } = [];
        public string? LastAgentMessage { get; set; }
        public string? LastError { get; set; }
    }
}
