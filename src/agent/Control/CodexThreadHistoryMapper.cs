using System.Text.Json;
using CodexControl.Protocol;

namespace CodexControl.Agent.Control;

internal static class CodexThreadHistoryMapper
{
    private const int MaxEntries = 200;
    private const int MaxEntryTextLength = 20_000;
    private const int MaxTotalTextLength = 200_000;
    private const int MaxThreadIdLength = 256;
    private const int MaxNameLength = 256;
    private const int MaxCwdLength = 2_048;

    public static CodexThreadReadResultPayload Map(JsonElement result)
    {
        if (!result.TryGetProperty("thread", out var thread) || thread.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("thread/read 未返回 thread 对象。");
        }

        var threadId = GetRequiredString(thread, "id", MaxThreadIdLength, "thread/read 未返回 thread.id。");
        if (!thread.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("thread/read 未返回 thread.turns 数组。");
        }

        var candidates = new List<CodexThreadHistoryEntryPayload>();
        var textWasTruncated = false;
        foreach (var turn in turns.EnumerateArray())
        {
            if (turn.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var turnId = GetOptionalString(turn, "id", MaxThreadIdLength);
            if (string.IsNullOrWhiteSpace(turnId) ||
                !turn.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (!TryMapEntry(item, turnId, out var entry, out var entryWasTruncated))
                {
                    continue;
                }

                textWasTruncated |= entryWasTruncated;
                candidates.Add(entry);
            }
        }

        var retained = new List<CodexThreadHistoryEntryPayload>(Math.Min(candidates.Count, MaxEntries));
        var totalTextLength = 0;
        for (var index = candidates.Count - 1; index >= 0 && retained.Count < MaxEntries; index--)
        {
            var candidate = candidates[index];
            if (retained.Count > 0 && totalTextLength + candidate.Text.Length > MaxTotalTextLength)
            {
                break;
            }

            retained.Add(candidate);
            totalTextLength += candidate.Text.Length;
        }

        retained.Reverse();
        return new CodexThreadReadResultPayload(
            threadId,
            GetOptionalString(thread, "name", MaxNameLength),
            GetOptionalString(thread, "cwd", MaxCwdLength),
            retained,
            textWasTruncated || retained.Count < candidates.Count);
    }

    private static bool TryMapEntry(
        JsonElement item,
        string turnId,
        out CodexThreadHistoryEntryPayload entry,
        out bool textWasTruncated)
    {
        entry = null!;
        textWasTruncated = false;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var itemId = GetOptionalString(item, "id", MaxThreadIdLength);
        var type = GetOptionalString(item, "type", 64);
        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        string? role = null;
        string? text = null;
        string? phase = null;
        if (type == "userMessage")
        {
            role = "user";
            text = MapUserContent(item);
        }
        else if (type == "agentMessage")
        {
            role = "assistant";
            text = GetOptionalString(item, "text", int.MaxValue);
            phase = GetOptionalString(item, "phase", 64);
        }

        if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (text.Length > MaxEntryTextLength)
        {
            text = string.Concat(text.AsSpan(0, MaxEntryTextLength - 1), "…");
            textWasTruncated = true;
        }

        entry = new CodexThreadHistoryEntryPayload(itemId, turnId, role, text, phase);
        return true;
    }

    private static string? MapUserContent(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var input in content.EnumerateArray())
        {
            if (input.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = GetOptionalString(input, "type", 64);
            var value = type switch
            {
                "text" => GetOptionalString(input, "text", int.MaxValue),
                "image" => "[图片]",
                "localImage" => FormatNamedInput("本地图片", input, "path"),
                "audio" => "[音频]",
                "localAudio" => FormatNamedInput("本地音频", input, "path"),
                "skill" => FormatNamedInput("技能", input, "name"),
                "mention" => FormatNamedInput("引用", input, "name"),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value);
            }
        }

        return parts.Count == 0 ? null : string.Join(Environment.NewLine, parts);
    }

    private static string FormatNamedInput(string label, JsonElement input, string propertyName)
    {
        var value = GetOptionalString(input, propertyName, 2_048);
        if (propertyName == "path" && !string.IsNullOrWhiteSpace(value))
        {
            value = Path.GetFileName(value);
        }

        return string.IsNullOrWhiteSpace(value) ? $"[{label}]" : $"[{label}: {value}]";
    }

    private static string GetRequiredString(
        JsonElement value,
        string propertyName,
        int maximumLength,
        string errorMessage) =>
        GetOptionalString(value, propertyName, maximumLength) is { Length: > 0 } text
            ? text
            : throw new JsonException(errorMessage);

    private static string? GetOptionalString(JsonElement value, string propertyName, int maximumLength)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = property.GetString();
        return text is null || text.Length <= maximumLength ? text : text[..maximumLength];
    }
}
