using System.Text.Json;
using CodexControl.Agent.Codex;
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
            }, RelayJson.Options),
            _ => JsonSerializer.SerializeToElement(new { }, RelayJson.Options),
        };

        return new CodexEventPayload(
            string.Concat("evt_", Guid.NewGuid().ToString("N")),
            snapshot.Revision,
            kind,
            FindString(message, "params", "threadId") ?? snapshot.ActiveThreadId,
            FindString(message, "params", "turn", "id") ??
            FindString(message, "params", "turnId") ??
            snapshot.ActiveTurnId,
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

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "...");
}
