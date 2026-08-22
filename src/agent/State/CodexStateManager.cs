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
    private ImmutableHashSet<string> _pendingApprovalIds = [];

    public CodexStateSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event Action<CodexStateSnapshot>? SnapshotChanged;

    public void MarkStarting() => Update(snapshot => snapshot with
    {
        Status = CodexActivityStatus.Starting,
        CurrentActivity = "Starting Codex app-server",
        StartedAt = DateTimeOffset.UtcNow,
        LastError = null,
    });

    public void MarkIdle() => Update(snapshot => snapshot with
    {
        Status = CodexActivityStatus.Idle,
        CurrentActivity = null,
        RunningCommand = null,
        LastError = null,
    });

    public void MarkOffline(string? reason = null) => Update(snapshot => snapshot with
    {
        Status = CodexActivityStatus.Offline,
        CurrentActivity = null,
        RunningCommand = null,
        LastError = reason,
    });

    public void MarkFailed(string reason) => Update(snapshot => snapshot with
    {
        Status = CodexActivityStatus.Failed,
        CurrentActivity = null,
        RunningCommand = null,
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
                    ApplyApprovalRequested(JsonRpcProtocol.GetIdKey(approvalId));
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
        Update(snapshot => snapshot with
        {
            ActiveThreadId = threadId ?? snapshot.ActiveThreadId,
            CurrentProject = project ?? snapshot.CurrentProject,
            LastError = null,
        });
    }

    private void ApplyTurnStarted(JsonElement message)
    {
        var threadId = FindString(message, "params", "threadId");
        var turnId = FindString(message, "params", "turn", "id") ??
                     FindString(message, "params", "turnId");
        Update(snapshot => snapshot with
        {
            Status = CodexActivityStatus.Thinking,
            ActiveThreadId = threadId ?? snapshot.ActiveThreadId,
            ActiveTurnId = turnId ?? snapshot.ActiveTurnId,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentActivity = "Codex is thinking",
            RunningCommand = null,
            LastError = null,
        });
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

        Update(snapshot => snapshot with
        {
            Status = status,
            CurrentActivity = itemType,
            RunningCommand = command,
        });
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
        Update(snapshot => snapshot.Status is CodexActivityStatus.Completed or
            CodexActivityStatus.Interrupted or
            CodexActivityStatus.Failed
            ? snapshot
            : snapshot with
            {
                Status = _pendingApprovalIds.Count > 0
                    ? CodexActivityStatus.WaitingApproval
                    : CodexActivityStatus.Thinking,
                CurrentActivity = _pendingApprovalIds.Count > 0 ? "Waiting for approval" : "Codex is thinking",
                RunningCommand = null,
                LastAgentMessage = agentMessage ?? snapshot.LastAgentMessage,
                ChangedFiles = changedFiles ?? snapshot.ChangedFiles,
            });
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
            _pendingApprovalIds = [];
            Publish(_snapshot with
            {
                Status = status,
                ActiveTurnId = null,
                CurrentActivity = null,
                RunningCommand = null,
                PendingApprovalCount = 0,
                LastError = status == CodexActivityStatus.Failed ? "Turn failed" : null,
            });
        }
    }

    private void ApplyApprovalRequested(string requestId)
    {
        lock (_gate)
        {
            _pendingApprovalIds = _pendingApprovalIds.Add(requestId);
            Publish(_snapshot with
            {
                Status = CodexActivityStatus.WaitingApproval,
                CurrentActivity = "Waiting for approval",
                PendingApprovalCount = _pendingApprovalIds.Count,
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
            if (!_pendingApprovalIds.Contains(requestId))
            {
                return;
            }

            _pendingApprovalIds = _pendingApprovalIds.Remove(requestId);
            Publish(_snapshot with
            {
                Status = _pendingApprovalIds.Count == 0
                    ? CodexActivityStatus.Thinking
                    : CodexActivityStatus.WaitingApproval,
                CurrentActivity = _pendingApprovalIds.Count == 0
                    ? "Codex is thinking"
                    : "Waiting for approval",
                PendingApprovalCount = _pendingApprovalIds.Count,
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

    private void Publish(CodexStateSnapshot snapshot)
    {
        var published = snapshot with
        {
            Revision = _snapshot.Revision + 1,
            LastActivityAt = DateTimeOffset.UtcNow,
        };
        Volatile.Write(ref _snapshot, published);
        InvokeSnapshotChanged(published);
    }

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
}
