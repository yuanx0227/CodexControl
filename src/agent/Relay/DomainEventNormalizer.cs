using System.Text.Json;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Control;
using CodexControl.Agent.State;
using CodexControl.Protocol;

namespace CodexControl.Agent.Relay;

internal static class DomainEventNormalizer
{
    public static CodexEventPayload? Normalize(JsonElement message, CodexStateSnapshot snapshot)
    {
        if (!JsonRpcProtocol.TryGetMethod(message, out var method))
        {
            return null;
        }

        var kind = method switch
        {
            "thread/started" => "AgentStarted",
            "turn/started" => "TurnStarted",
            "turn/completed" => "TurnCompleted",
            "item/agentMessage/delta" => "AgentMessageDelta",
            "item/started" when ItemType(message) == "commandExecution" => "CommandStarted",
            "item/completed" when ItemType(message) == "commandExecution" => "CommandCompleted",
            "item/completed" when ItemType(message) == "agentMessage" => "AgentMessageCompleted",
            "item/completed" when ItemType(message) == "fileChange" => "FileChanged",
            "error" => "ErrorOccurred",
            _ => null,
        };
        if (kind is null)
        {
            return null;
        }

        var data = kind switch
        {
            "TurnStarted" => JsonSerializer.SerializeToElement(new
            {
                startedAt = FindInt64(message, "params", "turn", "startedAt"),
            }, RelayJson.Options),
            "AgentMessageDelta" => JsonSerializer.SerializeToElement(new
            {
                delta = Truncate(FindString(message, "params", "delta"), 4000),
            }, RelayJson.Options),
            "CommandStarted" or "CommandCompleted" => JsonSerializer.SerializeToElement(new
            {
                command = Truncate(FindString(message, "params", "item", "command"), 1000),
                cwd = FindString(message, "params", "item", "cwd"),
                status = FindString(message, "params", "item", "status"),
            }, RelayJson.Options),
            "AgentMessageCompleted" => JsonSerializer.SerializeToElement(new
            {
                text = Truncate(FindString(message, "params", "item", "text"), 20_000),
            }, RelayJson.Options),
            "TurnCompleted" => JsonSerializer.SerializeToElement(new
            {
                status = FindString(message, "params", "turn", "status"),
                startedAt = FindInt64(message, "params", "turn", "startedAt"),
                completedAt = FindInt64(message, "params", "turn", "completedAt"),
                durationMs = FindInt64(message, "params", "turn", "durationMs"),
            }, RelayJson.Options),
            "FileChanged" => MapFileChangedData(message),
            _ => JsonSerializer.SerializeToElement(new { }, RelayJson.Options),
        };

        var threadId = FindString(message, "params", "threadId") ??
                       FindString(message, "params", "thread", "id") ??
                       snapshot.ActiveThreadId;
        var turnId = FindString(message, "params", "turn", "id") ??
                     FindString(message, "params", "turnId") ??
                     FindString(message, "params", "item", "turnId") ??
                     snapshot.ActiveTurns.FirstOrDefault(value => value.ThreadId == threadId)?.TurnId ??
                     (threadId == snapshot.ActiveThreadId ? snapshot.ActiveTurnId : null);

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
        return JsonSerializer.SerializeToElement(new
        {
            changes,
            paths = changes.Select(change => change.Path).ToArray(),
            status = FindString(message, "params", "item", "status"),
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

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "...");
}
