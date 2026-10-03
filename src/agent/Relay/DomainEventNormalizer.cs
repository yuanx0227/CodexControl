using System.Text.Json;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Control;
using CodexControl.Agent.State;
using CodexControl.Protocol;

namespace CodexControl.Agent.Relay;

internal static class DomainEventNormalizer
{
    // Fits below the Relay frame limit even when JSON escapes every UTF-16 code unit.
    private const int MaximumTextLength = 128_000;
    public static CodexEventPayload? Normalize(JsonElement message, CodexStateSnapshot snapshot)
    {
        if (!JsonRpcProtocol.TryGetMethod(message, out var method))
        {
            return null;
        }

        var kind = method switch
        {
            "thread/started" => "AgentStarted",
            "thread/status/changed" => "ThreadStatusChanged",
            "turn/started" => "TurnStarted",
            "turn/completed" => "TurnCompleted",
            "item/agentMessage/delta" => "AgentMessageDelta",
            "item/commandExecution/outputDelta" => "CommandOutputDelta",
            "turn/plan/updated" => "PlanUpdated",
            "item/reasoning/summaryTextDelta" => "ReasoningSummaryDelta",
            "thread/settings/updated" => "ThreadSettingsChanged",
            "item/started" when ItemType(message) == "commandExecution" => "CommandStarted",
            "item/completed" when ItemType(message) == "commandExecution" => "CommandCompleted",
            "item/completed" when ItemType(message) == "agentMessage" => "AgentMessageCompleted",
            "item/started" or "item/completed" when ItemType(message) == "userMessage" => "UserMessageCompleted",
            "item/completed" when ItemType(message) == "fileChange" => "FileChanged",
            "item/started" when ItemType(message) is "agentMessage" or "fileChange" or "reasoning" => null,
            "item/completed" when ItemType(message) == "reasoning" => null,
            "error" => "ErrorOccurred",
            "item/started" or "item/completed" => "UnsupportedItem",
            _ => null,
        };
        if (kind is null)
        {
            return null;
        }

        var data = kind switch
        {
            "ThreadStatusChanged" => JsonSerializer.SerializeToElement(new
            {
                status = FindString(message, "params", "status", "type"),
                activeFlags = TryFind(message, out var flags, "params", "status", "activeFlags") &&
                              flags.ValueKind == JsonValueKind.Array
                    ? flags.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString()).Where(value => value is "waitingOnApproval" or "waitingOnUserInput").ToArray()
                    : [],
            }, RelayJson.Options),
            "UserMessageCompleted" => MapUserMessageData(message),
            "TurnStarted" => JsonSerializer.SerializeToElement(new
            {
                startedAt = FindInt64(message, "params", "turn", "startedAt"),
            }, RelayJson.Options),
            "AgentMessageDelta" or "CommandOutputDelta" or "ReasoningSummaryDelta" =>
                MapText("delta", FindString(message, "params", "delta")),
            "CommandStarted" or "CommandCompleted" => MapCommand(message),
            "AgentMessageCompleted" => MapText("text", FindString(message, "params", "item", "text")),
            "PlanUpdated" => MapPlan(message),
            "TurnCompleted" => JsonSerializer.SerializeToElement(new
            {
                status = FindString(message, "params", "turn", "status"),
                startedAt = FindInt64(message, "params", "turn", "startedAt"),
                completedAt = FindInt64(message, "params", "turn", "completedAt"),
                durationMs = FindInt64(message, "params", "turn", "durationMs"),
            }, RelayJson.Options),
            "FileChanged" => MapFileChangedData(message),
            "UnsupportedItem" => JsonSerializer.SerializeToElement(new
            {
                itemType = Truncate(ItemType(message), 128), supported = false,
            }, RelayJson.Options),
            _ => JsonSerializer.SerializeToElement(new { }, RelayJson.Options),
        };

        var threadId = FindString(message, "params", "threadId") ??
                       FindString(message, "params", "thread", "id") ??
                       FindString(message, "params", "turn", "threadId") ??
                       FindString(message, "params", "item", "threadId");
        var turnId = FindString(message, "params", "turn", "id") ??
                     FindString(message, "params", "turnId") ??
                     FindString(message, "params", "item", "turnId");
        if (threadId is null && turnId is not null)
            threadId = snapshot.ActiveTurns.FirstOrDefault(value => value.TurnId == turnId)?.ThreadId;
        if (threadId is null || kind is not ("AgentStarted" or "ThreadStatusChanged" or "ThreadSettingsChanged") && turnId is null)
        {
            // Surface a metadata-only recovery signal. Never attach content to UI focus.
            kind = "UnattributedEvent";
            data = JsonSerializer.SerializeToElement(new { method, resyncRequired = true }, RelayJson.Options);
        }

        return new CodexEventPayload(
            string.Concat("evt_", Guid.NewGuid().ToString("N")),
            snapshot.Revision,
            kind,
            threadId,
            turnId,
            FindString(message, "params", "item", "id") ?? FindString(message, "params", "itemId"),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            data);
    }

    private static string? ItemType(JsonElement message) => FindString(message, "params", "item", "type");

    private static JsonElement MapUserMessageData(JsonElement message)
    {
        var content = TryFind(message, out var item, "params", "item")
            ? CodexThreadHistoryMapper.MapUserContent(item) : null;
        return JsonSerializer.SerializeToElement(new
        {
            text = Truncate(content?.Text, MaximumTextLength),
            attachments = content?.Attachments,
            truncated = content?.Text.Length > MaximumTextLength,
            originalLength = content?.Text.Length ?? 0,
            resyncRequired = content?.Text.Length > MaximumTextLength,
        }, RelayJson.Options);
    }

    private static JsonElement MapText(string field, string? text) => JsonSerializer.SerializeToElement(
        new Dictionary<string, object?>
        {
            [field] = Truncate(text, MaximumTextLength),
            ["truncated"] = text?.Length > MaximumTextLength,
            ["originalLength"] = text?.Length ?? 0,
            ["resyncRequired"] = text?.Length > MaximumTextLength,
        }, RelayJson.Options);

    private static JsonElement MapCommand(JsonElement message)
    {
        var command = FindString(message, "params", "item", "command");
        var output = FindString(message, "params", "item", "aggregatedOutput");
        return JsonSerializer.SerializeToElement(new
        {
            command = Truncate(command, MaximumTextLength / 2),
            output = Truncate(output, MaximumTextLength / 2),
            commandOriginalLength = command?.Length ?? 0,
            outputOriginalLength = output?.Length ?? 0,
            truncated = command?.Length > MaximumTextLength / 2 || output?.Length > MaximumTextLength / 2,
            cwd = FindString(message, "params", "item", "cwd"),
            status = FindString(message, "params", "item", "status"),
            exitCode = FindInt64(message, "params", "item", "exitCode"),
        }, RelayJson.Options);
    }

    private static JsonElement MapPlan(JsonElement message)
    {
        var plan = TryFind(message, out var values, "params", "plan") && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Take(100).Select(value => new
            {
                step = Truncate(FindString(value, "step"), 1000),
                status = FindString(value, "status"),
            }).ToArray() : [];
        var explanation = FindString(message, "params", "explanation");
        return JsonSerializer.SerializeToElement(new
        {
            plan,
            explanation = Truncate(explanation, 4000),
            truncated = explanation?.Length > 4000 ||
                (values.ValueKind == JsonValueKind.Array && (values.GetArrayLength() > 100 ||
                    values.EnumerateArray().Any(value => FindString(value, "step")?.Length > 1000))),
        }, RelayJson.Options);
    }

    private static string? FindString(JsonElement root, params string[] path)
    {
        var value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                return null;
            }
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static long? FindInt64(JsonElement root, params string[] path)
    {
        var value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                return null;
            }
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;
    }

    private static IReadOnlyList<CodexThreadHistoryFileChangePayload> FindChangedFiles(JsonElement message)
    {
        if (!TryFind(message, out var item, "params", "item"))
        {
            return [];
        }

        return CodexFileChangeMapper.Map(item);
    }

    private static JsonElement MapFileChangedData(JsonElement message)
    {
        var changes = FindChangedFiles(message);
        var sourceCount = TryFind(message, out var source, "params", "item", "changes") && source.ValueKind == JsonValueKind.Array
            ? source.GetArrayLength() : 0;
        return JsonSerializer.SerializeToElement(new
        {
            changes,
            paths = changes.Select(change => change.Path).ToArray(),
            status = FindString(message, "params", "item", "status"),
            diffIncluded = false,
            originalChangeCount = sourceCount,
            truncated = sourceCount > changes.Count || (source.ValueKind == JsonValueKind.Array &&
                source.EnumerateArray().Any(value => FindString(value, "diff")?.Length > 200_000 || FindString(value, "path")?.Length > 2_048)),
        }, RelayJson.Options);
    }

    private static bool TryFind(JsonElement root, out JsonElement value, params string[] path)
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

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength) return value;
        var length = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..length];
    }
}
