using System.Collections.Concurrent;
using System.Text.Json;
using CodexControl.Agent.Diagnostics;

namespace CodexControl.Agent.Codex;

public sealed record PendingApprovalSnapshot(
    string ApprovalId,
    string Method,
    string? ThreadId,
    string? TurnId,
    string? ItemId,
    string? Command,
    string? Cwd,
    string? Reason,
    IReadOnlyList<JsonElement> AvailableDecisions,
    DateTimeOffset RequestedAt,
    bool IsResolved,
    string? ResolvedBy);

public sealed record ApprovalResolutionResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// 对 app-server Approval Server Request 执行 first-valid-response-wins 仲裁。
/// </summary>
public sealed class ApprovalCoordinator
{
    private const int Pending = 0;
    private const int Resolving = 1;
    private const int Resolved = 2;

    private readonly ConcurrentDictionary<string, ApprovalEntry> _byRequestId = new();
    private readonly ConcurrentDictionary<string, ApprovalEntry> _byPublicId = new();
    private readonly Func<string, CancellationToken, ValueTask> _sendResponse;
    private readonly AgentLog _log;

    public ApprovalCoordinator(
        Func<string, CancellationToken, ValueTask> sendResponse,
        AgentLog log)
    {
        _sendResponse = sendResponse;
        _log = log;
    }

    public event Action<PendingApprovalSnapshot>? ApprovalRequested;

    public event Action<PendingApprovalSnapshot>? ApprovalResolved;

    public IReadOnlyList<PendingApprovalSnapshot> PendingApprovals =>
        _byPublicId.Values
            .Where(static entry => Volatile.Read(ref entry.Status) != Resolved)
            .OrderBy(static entry => entry.RequestedAt)
            .Select(static entry => entry.ToSnapshot())
            .ToArray();

    public void ObserveServerMessage(JsonElement message)
    {
        if (!JsonRpcProtocol.TryGetMethod(message, out var method))
        {
            return;
        }

        if (IsApprovalRequest(method) && JsonRpcProtocol.TryGetId(message, out var requestId))
        {
            RegisterApproval(method, requestId, message);
            return;
        }

        if (method == "serverRequest/resolved" &&
            TryFindElement(message, out var resolvedRequestId, "params", "requestId"))
        {
            MarkServerResolved(JsonRpcProtocol.GetIdKey(resolvedRequestId));
            return;
        }

        if (method == "turn/completed")
        {
            var threadId = FindString(message, "params", "threadId");
            var turnId = FindString(message, "params", "turn", "id") ??
                         FindString(message, "params", "turnId");
            foreach (var entry in _byPublicId.Values)
            {
                if (Volatile.Read(ref entry.Status) != Resolved &&
                    (threadId is null || entry.ThreadId == threadId) &&
                    (turnId is null || entry.TurnId == turnId))
                {
                    Complete(entry, "server-lifecycle");
                }
            }
        }
    }

    public async Task<bool> TryHandleTuiResponseAsync(
        JsonElement message,
        string rawMessage,
        CancellationToken cancellationToken)
    {
        if (!JsonRpcProtocol.IsResponse(message) || !JsonRpcProtocol.TryGetId(message, out var id))
        {
            return false;
        }

        if (!_byRequestId.TryGetValue(JsonRpcProtocol.GetIdKey(id), out var entry))
        {
            return false;
        }

        if (!IsValidResponse(entry, message))
        {
            _log.Warning("approval_invalid_tui_response", $"approval={entry.PublicId}; method={entry.Method}");
            return true;
        }

        if (Interlocked.CompareExchange(ref entry.Status, Resolving, Pending) != Pending)
        {
            _log.Info("approval_duplicate_suppressed", $"approval={entry.PublicId}; responder=tui");
            return true;
        }

        try
        {
            await _sendResponse(rawMessage, cancellationToken).ConfigureAwait(false);
            Complete(entry, "tui");
            return true;
        }
        catch
        {
            Interlocked.CompareExchange(ref entry.Status, Pending, Resolving);
            throw;
        }
    }

    public async Task<ApprovalResolutionResult> ResolveRemoteAsync(
        string approvalId,
        JsonElement decision,
        string responder,
        CancellationToken cancellationToken)
    {
        if (!_byPublicId.TryGetValue(approvalId, out var entry))
        {
            return new ApprovalResolutionResult(false, "APPROVAL_NOT_FOUND", "审批请求不存在或已过期。");
        }

        if (!IsDecisionAvailable(entry, decision))
        {
            return new ApprovalResolutionResult(
                false,
                "APPROVAL_DECISION_UNAVAILABLE",
                "该 Decision 不在 app-server 提供的可选项中。");
        }

        if (Interlocked.CompareExchange(ref entry.Status, Resolving, Pending) != Pending)
        {
            return new ApprovalResolutionResult(
                false,
                "APPROVAL_ALREADY_RESOLVED",
                "审批已由其他控制端处理。");
        }

        try
        {
            using var resultDocument = JsonDocument.Parse(BuildDecisionResult(decision));
            var response = JsonRpcProtocol.BuildResultResponse(entry.RequestId, resultDocument.RootElement);
            await _sendResponse(response, cancellationToken).ConfigureAwait(false);
            Complete(entry, responder);
            return new ApprovalResolutionResult(true, null, null);
        }
        catch
        {
            Interlocked.CompareExchange(ref entry.Status, Pending, Resolving);
            throw;
        }
    }

    private void RegisterApproval(string method, JsonElement requestId, JsonElement message)
    {
        var requestKey = JsonRpcProtocol.GetIdKey(requestId);
        if (_byRequestId.ContainsKey(requestKey))
        {
            return;
        }

        var publicId = string.Concat("apr_", Guid.NewGuid().ToString("N"));
        var availableDecisions = ReadAvailableDecisions(message);
        var entry = new ApprovalEntry(
            publicId,
            requestKey,
            requestId.Clone(),
            method,
            FindString(message, "params", "threadId"),
            FindString(message, "params", "turnId"),
            FindString(message, "params", "itemId"),
            FindString(message, "params", "command"),
            FindString(message, "params", "cwd"),
            FindString(message, "params", "reason"),
            availableDecisions,
            DateTimeOffset.UtcNow);

        if (!_byRequestId.TryAdd(requestKey, entry))
        {
            return;
        }

        _byPublicId[publicId] = entry;
        _log.Info("approval_requested", $"approval={publicId}; method={method}");
        InvokeSafely(ApprovalRequested, entry.ToSnapshot());
        PruneResolved();
    }

    private void MarkServerResolved(string requestKey)
    {
        if (_byRequestId.TryGetValue(requestKey, out var entry))
        {
            Complete(entry, entry.ResolvedBy ?? "server");
        }
    }

    private void Complete(ApprovalEntry entry, string resolvedBy)
    {
        if (Interlocked.Exchange(ref entry.Status, Resolved) == Resolved)
        {
            return;
        }

        entry.ResolvedBy = resolvedBy;
        entry.ResolvedAt = DateTimeOffset.UtcNow;
        _log.Info("approval_resolved", $"approval={entry.PublicId}; responder={resolvedBy}");
        InvokeSafely(ApprovalResolved, entry.ToSnapshot());
    }

    private bool IsValidResponse(ApprovalEntry entry, JsonElement message)
    {
        if (message.TryGetProperty("error", out _))
        {
            return true;
        }

        return message.TryGetProperty("result", out var result) &&
               result.ValueKind == JsonValueKind.Object &&
               result.TryGetProperty("decision", out var decision) &&
               IsDecisionAvailable(entry, decision);
    }

    private static bool IsDecisionAvailable(ApprovalEntry entry, JsonElement decision)
    {
        if (entry.AvailableDecisions.Count > 0)
        {
            return entry.AvailableDecisions.Any(available => JsonEquals(available, decision));
        }

        if (decision.ValueKind == JsonValueKind.Object &&
            !entry.Method.Contains("fileChange", StringComparison.Ordinal))
        {
            var properties = decision.EnumerateObject().ToArray();
            return properties.Length == 1 &&
                   properties[0].Name is "acceptWithExecpolicyAmendment" or "applyNetworkPolicyAmendment";
        }

        if (decision.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = decision.GetString();
        return entry.Method.Contains("fileChange", StringComparison.Ordinal)
            ? value is "accept" or "acceptForSession" or "decline" or "cancel"
            : value is "accept" or "acceptForSession" or "decline" or "cancel";
    }

    private static IReadOnlyList<JsonElement> ReadAvailableDecisions(JsonElement message)
    {
        if (!TryFindElement(message, out var decisions, "params", "availableDecisions") ||
            decisions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return decisions.EnumerateArray().Select(static decision => decision.Clone()).ToArray();
    }

    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        return left.ValueKind switch
        {
            JsonValueKind.Object => ObjectEquals(left, right),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() &&
                                   left.EnumerateArray().Zip(right.EnumerateArray())
                                       .All(static pair => JsonEquals(pair.First, pair.Second)),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.GetRawText() == right.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null or JsonValueKind.Undefined => true,
            _ => left.GetRawText() == right.GetRawText(),
        };
    }

    private static bool ObjectEquals(JsonElement left, JsonElement right)
    {
        var leftProperties = left.EnumerateObject().ToArray();
        var rightProperties = right.EnumerateObject().ToArray();
        if (leftProperties.Length != rightProperties.Length)
        {
            return false;
        }

        foreach (var property in leftProperties)
        {
            if (!right.TryGetProperty(property.Name, out var rightValue) ||
                !JsonEquals(property.Value, rightValue))
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildDecisionResult(JsonElement decision)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("decision");
            decision.WriteTo(writer);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool IsApprovalRequest(string method) =>
        method.EndsWith("/requestApproval", StringComparison.Ordinal) ||
        method is "applyPatchApproval" or "execCommandApproval";

    private void PruneResolved()
    {
        if (_byPublicId.Count <= 1024)
        {
            return;
        }

        var threshold = DateTimeOffset.UtcNow.AddHours(-1);
        foreach (var entry in _byPublicId.Values)
        {
            if (Volatile.Read(ref entry.Status) == Resolved && entry.ResolvedAt < threshold)
            {
                _byPublicId.TryRemove(entry.PublicId, out _);
                _byRequestId.TryRemove(entry.RequestKey, out _);
            }
        }
    }

    private static void InvokeSafely(
        Action<PendingApprovalSnapshot>? handlers,
        PendingApprovalSnapshot snapshot)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<PendingApprovalSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(snapshot);
            }
            catch
            {
                // 订阅者不能破坏协议读循环。
            }
        }
    }

    private static string? FindString(JsonElement root, params string[] path) =>
        TryFindElement(root, out var value, path) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

    private sealed class ApprovalEntry
    {
        public ApprovalEntry(
            string publicId,
            string requestKey,
            JsonElement requestId,
            string method,
            string? threadId,
            string? turnId,
            string? itemId,
            string? command,
            string? cwd,
            string? reason,
            IReadOnlyList<JsonElement> availableDecisions,
            DateTimeOffset requestedAt)
        {
            PublicId = publicId;
            RequestKey = requestKey;
            RequestId = requestId;
            Method = method;
            ThreadId = threadId;
            TurnId = turnId;
            ItemId = itemId;
            Command = command;
            Cwd = cwd;
            Reason = reason;
            AvailableDecisions = availableDecisions;
            RequestedAt = requestedAt;
        }

        public string PublicId { get; }
        public string RequestKey { get; }
        public JsonElement RequestId { get; }
        public string Method { get; }
        public string? ThreadId { get; }
        public string? TurnId { get; }
        public string? ItemId { get; }
        public string? Command { get; }
        public string? Cwd { get; }
        public string? Reason { get; }
        public IReadOnlyList<JsonElement> AvailableDecisions { get; }
        public DateTimeOffset RequestedAt { get; }
        public int Status;
        public string? ResolvedBy;
        public DateTimeOffset? ResolvedAt;

        public PendingApprovalSnapshot ToSnapshot() => new(
            PublicId,
            Method,
            ThreadId,
            TurnId,
            ItemId,
            Command,
            Cwd,
            Reason,
            AvailableDecisions,
            RequestedAt,
            Volatile.Read(ref Status) == Resolved,
            ResolvedBy);
    }
}
