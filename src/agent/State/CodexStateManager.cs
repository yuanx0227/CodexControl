using System.Collections.Immutable;
using System.Text.Json;
using CodexControl.Agent.Codex;

namespace CodexControl.Agent.State;

/// <summary>
/// Codex 状态的唯一写入点。外部只能读取不可变快照。
/// </summary>
public sealed class CodexStateManager
{
    private readonly object _gate = new();
    private CodexStateSnapshot _snapshot = CodexStateSnapshot.Initial;
    private ImmutableDictionary<string, CodexActiveTurnSnapshot> _activeTurns =
        ImmutableDictionary<string, CodexActiveTurnSnapshot>.Empty.WithComparers(StringComparer.Ordinal);
    private ImmutableDictionary<string, string> _threadProjects =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
    private ImmutableDictionary<string, PendingApprovalContext> _pendingApprovals =
        ImmutableDictionary<string, PendingApprovalContext>.Empty.WithComparers(StringComparer.Ordinal);

    public CodexStateSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event Action<CodexStateSnapshot>? SnapshotChanged;

    public void MarkStarting() => ResetActivity(snapshot => snapshot with
    {
        Status = CodexActivityStatus.Starting,
        ActiveThreadId = null,
        ActiveTurnId = null,
        StartedAt = null,
        CurrentActivity = "Starting Codex app-server",
        RunningCommand = null,
        PendingApprovalCount = 0,
        LastError = null,
    });

    public void MarkIdle() => ResetActivity(snapshot => snapshot with
    {
        Status = CodexActivityStatus.Idle,
        ActiveThreadId = null,
        ActiveTurnId = null,
        StartedAt = null,
        CurrentActivity = null,
        RunningCommand = null,
        PendingApprovalCount = 0,
        LastError = null,
    });

    public void MarkOffline(string? reason = null) => ResetActivity(snapshot => snapshot with
    {
        Status = CodexActivityStatus.Offline,
        ActiveThreadId = null,
        ActiveTurnId = null,
        StartedAt = null,
        CurrentActivity = null,
        RunningCommand = null,
        PendingApprovalCount = 0,
        LastError = reason,
    });

    public void MarkFailed(string reason) => ResetActivity(snapshot => snapshot with
    {
        Status = CodexActivityStatus.Failed,
        ActiveThreadId = null,
        ActiveTurnId = null,
        StartedAt = null,
        CurrentActivity = null,
        RunningCommand = null,
        PendingApprovalCount = 0,
        LastError = reason,
    });

    public void SetRelayConnected(bool connected) => Update(snapshot => snapshot with
    {
        RelayConnected = connected,
    });

    public void SetPairedControllerCount(int count) => Update(snapshot => snapshot with
    {
        PairedControllerCount = Math.Max(0, count),
    });

    public void ApplyServerMessage(JsonElement message)
    {
        if (!JsonRpcProtocol.TryGetMethod(message, out var method))
        {
            return;
        }

        switch (method)
        {
            case "thread/started":
                ApplyThreadStarted(message);
                break;
            case "turn/started":
                ApplyTurnStarted(message);
                break;
            case "thread/status/changed":
                ApplyThreadStatusChanged(message);
                break;
            case "item/started":
                ApplyItemStarted(message);
                break;
            case "item/completed":
                ApplyItemCompleted(message);
                break;
            case "turn/completed":
                ApplyTurnCompleted(message);
                break;
            case "serverRequest/resolved":
                ApplyServerRequestResolved(message);
                break;
            default:
                if (method.EndsWith("/requestApproval", StringComparison.Ordinal) &&
                    JsonRpcProtocol.TryGetId(message, out var approvalId))
                {
                    ApplyApprovalRequested(JsonRpcProtocol.GetIdKey(approvalId), message);
                }

                break;
        }
    }

    public void ApplyClientMessage(JsonElement message)
    {
        if (JsonRpcProtocol.IsResponse(message) && JsonRpcProtocol.TryGetId(message, out var id))
        {
            ResolveApproval(JsonRpcProtocol.GetIdKey(id));
        }
    }

    private void ApplyThreadStarted(JsonElement message)
    {
        var threadId = FindString(message, "params", "thread", "id") ??
                       FindString(message, "params", "threadId");
        var project = FindString(message, "params", "thread", "cwd");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(project))
            {
                _threadProjects = _threadProjects.SetItem(threadId, project);
                if (_activeTurns.TryGetValue(threadId, out var active))
                {
                    _activeTurns = _activeTurns.SetItem(
                        threadId,
                        active with { CurrentProject = project });
                }
            }

            Publish(_activeTurns.Count == 0
                ? _snapshot with
                {
                    ActiveThreadId = threadId,
                    ActiveTurnId = null,
                    CurrentProject = project ?? _snapshot.CurrentProject,
                    LastError = null,
                }
                : _snapshot with { LastError = null });
        }
    }

    private void ApplyTurnStarted(JsonElement message)
    {
        var threadId = FindThreadId(message);
        var turnId = FindTurnId(message);
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId))
        {
            return;
        }

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var active = new CodexActiveTurnSnapshot(
                threadId,
                turnId,
                CodexActivityStatus.Thinking,
                now,
                now,
                _threadProjects.GetValueOrDefault(threadId) ?? _snapshot.CurrentProject,
                "Codex is thinking",
                null,
                [],
                PendingApprovalCount(threadId, turnId),
                null,
                null);
            _activeTurns = _activeTurns.SetItem(threadId, active);
            Publish(Focus(_snapshot, active));
        }
    }

    private void ApplyThreadStatusChanged(JsonElement message)
    {
        var threadId = FindThreadId(message);
        var type = FindString(message, "params", "status", "type");
        if (string.IsNullOrWhiteSpace(threadId)) return;

        lock (_gate)
        {
            if (!_activeTurns.TryGetValue(threadId, out var active)) return;
            if (type == "active")
            {
                var flags = TryFindElement(message, out var values, "params", "status", "activeFlags") &&
                            values.ValueKind == JsonValueKind.Array
                    ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString()).ToArray()
                    : [];
                var status = flags.Contains("waitingOnApproval")
                    ? CodexActivityStatus.WaitingApproval
                    : flags.Contains("waitingOnUserInput")
                        ? CodexActivityStatus.WaitingUserInput
                        : active.Status is CodexActivityStatus.WaitingApproval or CodexActivityStatus.WaitingUserInput
                            ? CodexActivityStatus.Thinking
                            : active.Status;
                var updated = active with
                {
                    Status = status,
                    LastActivityAt = DateTimeOffset.UtcNow,
                    CurrentActivity = status == CodexActivityStatus.WaitingApproval ? "Waiting for approval"
                        : status == CodexActivityStatus.WaitingUserInput ? "Waiting for user input"
                        : status == CodexActivityStatus.Thinking ? "Codex is thinking" : active.CurrentActivity,
                };
                _activeTurns = _activeTurns.SetItem(threadId, updated);
                Publish(Focus(_snapshot, updated));
            }
            else if (type is "idle" or "notLoaded" or "systemError")
            {
                _activeTurns = _activeTurns.Remove(threadId);
                _pendingApprovals = _pendingApprovals.RemoveRange(_pendingApprovals
                    .Where(entry => entry.Value.ThreadId == threadId).Select(entry => entry.Key));
                var next = _activeTurns.Values.OrderByDescending(value => value.LastActivityAt).FirstOrDefault();
                Publish(next is not null ? Focus(_snapshot, next) : _snapshot with
                {
                    Status = type == "systemError" ? CodexActivityStatus.Failed : CodexActivityStatus.Idle,
                    ActiveThreadId = threadId,
                    ActiveTurnId = null,
                    CurrentActivity = null,
                    RunningCommand = null,
                    LastError = type == "systemError" ? "Thread system error" : null,
                });
            }
        }
    }

    private void ApplyItemStarted(JsonElement message)
    {
        var itemType = FindString(message, "params", "item", "type");
        var command = FindString(message, "params", "item", "command");
        var status = itemType switch
        {
            "commandExecution" when LooksLikeTestCommand(command) => CodexActivityStatus.RunningTests,
            "commandExecution" => CodexActivityStatus.RunningCommand,
            "fileChange" => CodexActivityStatus.Editing,
            "reasoning" => CodexActivityStatus.Thinking,
            "agentMessage" => CodexActivityStatus.Thinking,
            _ => CodexActivityStatus.Reading,
        };

        lock (_gate)
        {
            if (TryResolveActiveTurn(message, out var threadId, out var active))
            {
                var updated = active with
                {
                    Status = status,
                    LastActivityAt = DateTimeOffset.UtcNow,
                    CurrentActivity = itemType,
                    RunningCommand = command,
                    LastError = null,
                };
                _activeTurns = _activeTurns.SetItem(threadId, updated);
                Publish(Focus(_snapshot, updated));
                return;
            }

            Publish(_snapshot with
            {
                Status = status,
                CurrentActivity = itemType,
                RunningCommand = command,
            });
        }
    }

    private void ApplyItemCompleted(JsonElement message)
    {
        var itemType = FindString(message, "params", "item", "type");
        var agentMessage = itemType == "agentMessage"
            ? FindString(message, "params", "item", "text")
            : null;
        var changedFiles = itemType == "fileChange"
            ? ReadChangedFiles(message)
            : null;
        lock (_gate)
        {
            if (TryResolveActiveTurn(message, out var threadId, out var active))
            {
                var pendingCount = PendingApprovalCount(active.ThreadId, active.TurnId);
                var updated = active with
                {
                    Status = pendingCount > 0
                        ? CodexActivityStatus.WaitingApproval
                        : CodexActivityStatus.Thinking,
                    LastActivityAt = DateTimeOffset.UtcNow,
                    CurrentActivity = pendingCount > 0 ? "Waiting for approval" : "Codex is thinking",
                    RunningCommand = null,
                    LastAgentMessage = agentMessage ?? active.LastAgentMessage,
                    ChangedFiles = changedFiles ?? active.ChangedFiles,
                    PendingApprovalCount = pendingCount,
                };
                _activeTurns = _activeTurns.SetItem(threadId, updated);
                Publish(Focus(_snapshot, updated));
                return;
            }

            if (_snapshot.Status is CodexActivityStatus.Completed or
                CodexActivityStatus.Interrupted or CodexActivityStatus.Failed)
            {
                return;
            }

            Publish(_snapshot with
            {
                Status = _pendingApprovals.Count > 0
                    ? CodexActivityStatus.WaitingApproval
                    : CodexActivityStatus.Thinking,
                CurrentActivity = _pendingApprovals.Count > 0 ? "Waiting for approval" : "Codex is thinking",
                RunningCommand = null,
                LastAgentMessage = agentMessage ?? _snapshot.LastAgentMessage,
                ChangedFiles = changedFiles ?? _snapshot.ChangedFiles,
            });
        }
    }

    private void ApplyTurnCompleted(JsonElement message)
    {
        var rawStatus = FindString(message, "params", "turn", "status") ??
                        FindString(message, "params", "status");
        var status = rawStatus switch
        {
            "interrupted" => CodexActivityStatus.Interrupted,
            "failed" => CodexActivityStatus.Failed,
            _ => CodexActivityStatus.Completed,
        };

        lock (_gate)
        {
            var found = TryResolveActiveTurn(message, out var threadId, out var completed);
            // A delayed completion from an earlier turn must not clear the current turn or its approvals.
            if (!found && _activeTurns.ContainsKey(threadId)) return;
            var turnId = FindTurnId(message) ?? (found ? completed.TurnId : null);
            if (found)
            {
                _activeTurns = _activeTurns.Remove(threadId);
                _threadProjects = _threadProjects.Remove(threadId);
            }

            if (!string.IsNullOrWhiteSpace(threadId) || !string.IsNullOrWhiteSpace(turnId))
            {
                _pendingApprovals = _pendingApprovals.RemoveRange(
                    _pendingApprovals
                        .Where(entry =>
                            (string.IsNullOrWhiteSpace(threadId) || entry.Value.ThreadId == threadId) &&
                            (string.IsNullOrWhiteSpace(turnId) || entry.Value.TurnId == turnId))
                        .Select(static entry => entry.Key));
            }

            var next = _activeTurns.Values
                .OrderByDescending(static value => value.LastActivityAt)
                .FirstOrDefault();
            if (next is not null)
            {
                Publish(Focus(_snapshot, next));
                return;
            }

            Publish(_snapshot with
            {
                Status = status,
                ActiveThreadId = found ? completed.ThreadId : FindThreadId(message) ?? _snapshot.ActiveThreadId,
                ActiveTurnId = null,
                StartedAt = found ? completed.StartedAt : _snapshot.StartedAt,
                CurrentProject = found ? completed.CurrentProject : _snapshot.CurrentProject,
                CurrentActivity = null,
                RunningCommand = null,
                PendingApprovalCount = 0,
                ChangedFiles = found ? completed.ChangedFiles : _snapshot.ChangedFiles,
                LastAgentMessage = found ? completed.LastAgentMessage : _snapshot.LastAgentMessage,
                LastError = status == CodexActivityStatus.Failed ? "Turn failed" : null,
            });
        }
    }

    private void ApplyApprovalRequested(string requestId, JsonElement message)
    {
        lock (_gate)
        {
            var threadId = FindThreadId(message);
            var turnId = FindTurnId(message);
            if (string.IsNullOrWhiteSpace(threadId) && _activeTurns.Count == 1)
            {
                var only = _activeTurns.Values.Single();
                threadId = only.ThreadId;
                turnId ??= only.TurnId;
            }

            _pendingApprovals = _pendingApprovals.SetItem(
                requestId,
                new PendingApprovalContext(threadId, turnId));
            if (!string.IsNullOrWhiteSpace(threadId) &&
                _activeTurns.TryGetValue(threadId, out var active))
            {
                var updated = active with
                {
                    Status = CodexActivityStatus.WaitingApproval,
                    LastActivityAt = DateTimeOffset.UtcNow,
                    CurrentActivity = "Waiting for approval",
                    PendingApprovalCount = PendingApprovalCount(active.ThreadId, active.TurnId),
                };
                _activeTurns = _activeTurns.SetItem(threadId, updated);
                Publish(Focus(_snapshot, updated));
                return;
            }

            Publish(_snapshot with
            {
                Status = CodexActivityStatus.WaitingApproval,
                CurrentActivity = "Waiting for approval",
                PendingApprovalCount = _pendingApprovals.Count,
            });
        }
    }

    private void ApplyServerRequestResolved(JsonElement message)
    {
        if (TryFindElement(message, out var requestId, "params", "requestId"))
        {
            ResolveApproval(JsonRpcProtocol.GetIdKey(requestId));
        }
    }

    private void ResolveApproval(string requestId)
    {
        lock (_gate)
        {
            if (!_pendingApprovals.TryGetValue(requestId, out var context))
            {
                return;
            }

            _pendingApprovals = _pendingApprovals.Remove(requestId);
            if (!string.IsNullOrWhiteSpace(context.ThreadId) &&
                _activeTurns.TryGetValue(context.ThreadId, out var active))
            {
                var pendingCount = PendingApprovalCount(active.ThreadId, active.TurnId);
                var updated = active with
                {
                    Status = pendingCount == 0
                        ? CodexActivityStatus.Thinking
                        : CodexActivityStatus.WaitingApproval,
                    LastActivityAt = DateTimeOffset.UtcNow,
                    CurrentActivity = pendingCount == 0
                        ? "Codex is thinking"
                        : "Waiting for approval",
                    PendingApprovalCount = pendingCount,
                };
                _activeTurns = _activeTurns.SetItem(active.ThreadId, updated);
                Publish(Focus(_snapshot, updated));
                return;
            }

            Publish(_snapshot with
            {
                Status = _pendingApprovals.Count == 0
                    ? CodexActivityStatus.Thinking
                    : CodexActivityStatus.WaitingApproval,
                CurrentActivity = _pendingApprovals.Count == 0
                    ? "Codex is thinking"
                    : "Waiting for approval",
                PendingApprovalCount = _pendingApprovals.Count,
            });
        }
    }

    private void Update(Func<CodexStateSnapshot, CodexStateSnapshot> transform)
    {
        lock (_gate)
        {
            Publish(transform(_snapshot));
        }
    }

    private void ResetActivity(Func<CodexStateSnapshot, CodexStateSnapshot> transform)
    {
        lock (_gate)
        {
            _activeTurns = _activeTurns.Clear();
            _threadProjects = _threadProjects.Clear();
            _pendingApprovals = _pendingApprovals.Clear();
            Publish(transform(_snapshot));
        }
    }

    private void Publish(CodexStateSnapshot snapshot)
    {
        var published = snapshot with
        {
            Revision = _snapshot.Revision + 1,
            LastActivityAt = DateTimeOffset.UtcNow,
            ActiveTurns = _activeTurns.Values
                .OrderByDescending(static value => value.LastActivityAt)
                .ToArray(),
            PendingApprovalCount = _pendingApprovals.Count,
        };
        Volatile.Write(ref _snapshot, published);
        InvokeSnapshotChanged(published);
    }

    private CodexStateSnapshot Focus(
        CodexStateSnapshot snapshot,
        CodexActiveTurnSnapshot active) => snapshot with
    {
        Status = active.Status,
        ActiveThreadId = active.ThreadId,
        ActiveTurnId = active.TurnId,
        StartedAt = active.StartedAt,
        CurrentProject = active.CurrentProject,
        CurrentActivity = active.CurrentActivity,
        RunningCommand = active.RunningCommand,
        ChangedFiles = active.ChangedFiles,
        PendingApprovalCount = _pendingApprovals.Count,
        LastAgentMessage = active.LastAgentMessage,
        LastError = active.LastError,
    };

    private bool TryResolveActiveTurn(
        JsonElement message,
        out string threadId,
        out CodexActiveTurnSnapshot active)
    {
        var requestedThreadId = FindThreadId(message);
        var requestedTurnId = FindTurnId(message);
        if (!string.IsNullOrWhiteSpace(requestedThreadId) &&
            _activeTurns.TryGetValue(requestedThreadId, out active!) &&
            (string.IsNullOrWhiteSpace(requestedTurnId) || active.TurnId == requestedTurnId))
        {
            threadId = requestedThreadId;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(requestedTurnId))
        {
            active = _activeTurns.Values.FirstOrDefault(value => value.TurnId == requestedTurnId)!;
            if (active is not null)
            {
                threadId = active.ThreadId;
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(requestedThreadId) &&
            string.IsNullOrWhiteSpace(requestedTurnId) &&
            _activeTurns.Count == 1)
        {
            active = _activeTurns.Values.Single();
            threadId = active.ThreadId;
            return true;
        }

        threadId = requestedThreadId ?? string.Empty;
        active = null!;
        return false;
    }

    private int PendingApprovalCount(string threadId, string turnId) =>
        _pendingApprovals.Values.Count(value =>
            value.ThreadId == threadId &&
            (string.IsNullOrWhiteSpace(value.TurnId) || value.TurnId == turnId));

    private void InvokeSnapshotChanged(CodexStateSnapshot snapshot)
    {
        var handlers = SnapshotChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<CodexStateSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(snapshot);
            }
            catch
            {
                // 观察者只能消费不可变快照，不能破坏状态机。
            }
        }
    }

    private static IReadOnlyList<string> ReadChangedFiles(JsonElement message)
    {
        if (!TryFindElement(message, out var changes, "params", "item", "changes") ||
            changes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return changes.EnumerateArray()
            .Select(change => change.ValueKind == JsonValueKind.Object &&
                              change.TryGetProperty("path", out var path) &&
                              path.ValueKind == JsonValueKind.String
                ? path.GetString()
                : null)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
    }

    private static bool LooksLikeTestCommand(string? command) =>
        command?.Contains(" test", StringComparison.OrdinalIgnoreCase) == true ||
        command?.StartsWith("test", StringComparison.OrdinalIgnoreCase) == true;

    private static string? FindThreadId(JsonElement message) =>
        FindString(message, "params", "threadId") ??
        FindString(message, "params", "thread", "id") ??
        FindString(message, "params", "turn", "threadId") ??
        FindString(message, "params", "item", "threadId");

    private static string? FindTurnId(JsonElement message) =>
        FindString(message, "params", "turn", "id") ??
        FindString(message, "params", "turnId") ??
        FindString(message, "params", "item", "turnId");

    private static string? FindString(JsonElement root, params string[] path)
    {
        return TryFindElement(root, out var value, path) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryFindElement(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }

    private sealed record PendingApprovalContext(string? ThreadId, string? TurnId);
}
