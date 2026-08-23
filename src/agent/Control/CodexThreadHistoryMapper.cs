using System.Text.Json;
using CodexControl.Protocol;

namespace CodexControl.Agent.Control;

internal sealed record CodexThreadMetadata(
    string ThreadId,
    string? Name,
    string? Cwd);

internal sealed record CodexThreadItemsPage(
    IReadOnlyList<CodexThreadHistoryEntryPayload> Entries,
    string? NextCursor,
    bool TextWasTruncated);

internal sealed record CodexTurnTimingPage(
    IReadOnlyList<CodexTurnTimingPayload> Turns,
    string? NextCursor);

internal sealed record MappedUserContent(
    string Text,
    IReadOnlyList<CodexThreadHistoryAttachmentPayload> Attachments);

internal static class CodexThreadHistoryMapper
{
    public const int EntryLimit = 200;

    private const int MaxEntryTextLength = 20_000;
    private const int MaxTotalTextLength = 200_000;
    private const int MaxThreadIdLength = 256;
    private const int MaxNameLength = 256;
    private const int MaxCwdLength = 2_048;

    public static CodexThreadMetadata MapMetadata(JsonElement result)
    {
        if (!result.TryGetProperty("thread", out var thread) || thread.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("thread/read 未返回 thread 对象。");
        }

        return new CodexThreadMetadata(
            GetRequiredString(thread, "id", MaxThreadIdLength, "thread/read 未返回 thread.id。"),
            GetOptionalString(thread, "name", MaxNameLength),
            GetOptionalString(thread, "cwd", MaxCwdLength));
    }

    public static CodexThreadItemsPage MapItemsPage(JsonElement result)
    {
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("thread/items/list 未返回 data 数组。");
        }

        var entries = new List<CodexThreadHistoryEntryPayload>();
        var textWasTruncated = false;
        foreach (var itemEntry in data.EnumerateArray())
        {
            if (itemEntry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var turnId = GetOptionalString(itemEntry, "turnId", MaxThreadIdLength);
            if (string.IsNullOrWhiteSpace(turnId) ||
                !itemEntry.TryGetProperty("item", out var item) ||
                !TryMapEntry(item, turnId, out var entry, out var entryWasTruncated))
            {
                continue;
            }

            textWasTruncated |= entryWasTruncated;
            entries.Add(entry);
        }

        return new CodexThreadItemsPage(
            entries,
            GetOptionalString(result, "nextCursor", 2_048),
            textWasTruncated);
    }

    public static CodexThreadReadResultPayload MapLegacy(JsonElement result)
    {
        var metadata = MapMetadata(result);
        var thread = result.GetProperty("thread");
        if (!thread.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("thread/read 未返回 thread.turns 数组。");
        }

        var entriesChronological = new List<CodexThreadHistoryEntryPayload>();
        var timings = new List<CodexTurnTimingPayload>();
        var textWasTruncated = false;
        foreach (var turn in turns.EnumerateArray())
        {
            if (turn.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var turnId = GetOptionalString(turn, "id", MaxThreadIdLength);
            if (!string.IsNullOrWhiteSpace(turnId))
            {
                timings.Add(MapTurnTiming(turn, turnId));
            }
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
                entriesChronological.Add(entry);
            }
        }

        entriesChronological.Reverse();
        return Build(
            metadata,
            entriesChronological,
            timings,
            hasMore: false,
            textWasTruncated);
    }

    public static CodexTurnTimingPage MapTurnsPage(JsonElement result)
    {
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("thread/turns/list 未返回 data 数组。");
        }

        var turns = new List<CodexTurnTimingPayload>();
        foreach (var turn in data.EnumerateArray())
        {
            if (turn.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var turnId = GetOptionalString(turn, "id", MaxThreadIdLength);
            if (!string.IsNullOrWhiteSpace(turnId))
            {
                turns.Add(MapTurnTiming(turn, turnId));
            }
        }

        return new CodexTurnTimingPage(turns, GetOptionalString(result, "nextCursor", 2_048));
    }

    public static CodexThreadReadResultPayload Build(
        CodexThreadMetadata metadata,
        IReadOnlyList<CodexThreadHistoryEntryPayload> entriesNewestFirst,
        IReadOnlyList<CodexTurnTimingPayload> turns,
        bool hasMore,
        bool textWasTruncated)
    {
        var retainedNewestFirst = new List<CodexThreadHistoryEntryPayload>(
            Math.Min(entriesNewestFirst.Count, EntryLimit));
        var totalTextLength = 0;
        var totalAttachmentLength = 0;
        foreach (var candidate in entriesNewestFirst)
        {
            if (retainedNewestFirst.Count >= EntryLimit ||
                (retainedNewestFirst.Count > 0 && totalTextLength + candidate.Text.Length > MaxTotalTextLength))
            {
                break;
            }

            var attachmentLength = candidate.Attachments.Sum(value => value.DataUrl.Length);
            var retained = totalAttachmentLength + attachmentLength <=
                           CodexImageAttachmentMapper.MaxTotalDataUrlLength
                ? candidate
                : candidate with { Attachments = [] };
            retainedNewestFirst.Add(retained);
            totalTextLength += candidate.Text.Length;
            totalAttachmentLength += retained.Attachments.Sum(value => value.DataUrl.Length);
        }

        retainedNewestFirst.Reverse();
        return new CodexThreadReadResultPayload(
            metadata.ThreadId,
            metadata.Name,
            metadata.Cwd,
            retainedNewestFirst,
            turns,
            hasMore || textWasTruncated || retainedNewestFirst.Count < entriesNewestFirst.Count);
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
        IReadOnlyList<CodexThreadHistoryAttachmentPayload> attachments = [];
        IReadOnlyList<CodexThreadHistoryFileChangePayload> changes = [];
        if (type == "userMessage")
        {
            role = "user";
            var mapped = MapUserContent(item);
            text = mapped?.Text;
            attachments = mapped?.Attachments ?? [];
        }
        else if (type == "agentMessage")
        {
            role = "assistant";
            text = GetOptionalString(item, "text", int.MaxValue);
            phase = GetOptionalString(item, "phase", 64);
        }
        else if (type == "commandExecution")
        {
            role = "tool";
            text = GetOptionalString(item, "command", int.MaxValue);
            phase = GetOptionalString(item, "status", 64) ?? "command";
        }
        else if (type == "fileChange")
        {
            role = "tool";
            changes = CodexFileChangeMapper.Map(item);
            text = CodexFileChangeMapper.Format(changes);
            phase = GetOptionalString(item, "status", 64) ?? "fileChange";
        }

        if (string.IsNullOrWhiteSpace(role) ||
            string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
        {
            return false;
        }

        text = text?.Trim() ?? string.Empty;
        if (text.Length > MaxEntryTextLength)
        {
            text = string.Concat(text.AsSpan(0, MaxEntryTextLength - 1), "…");
            textWasTruncated = true;
        }

        entry = new CodexThreadHistoryEntryPayload(itemId, turnId, role, text, phase, attachments, changes);
        return true;
    }

    private static MappedUserContent? MapUserContent(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        var attachments = new List<CodexThreadHistoryAttachmentPayload>();
        foreach (var input in content.EnumerateArray())
        {
            if (input.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = GetOptionalString(input, "type", 64);
            string? value;
            switch (type)
            {
                case "text":
                    value = GetOptionalString(input, "text", int.MaxValue);
                    break;
                case "image":
                    value = TryAddAttachment(
                        attachments,
                        CodexImageAttachmentMapper.TryMapDataUrl(
                            GetOptionalString(input, "url", int.MaxValue),
                            out var remoteImage),
                        remoteImage)
                        ? null
                        : "[图片]";
                    break;
                case "localImage":
                    value = TryAddAttachment(
                        attachments,
                        CodexImageAttachmentMapper.TryMapLocal(
                            GetOptionalString(input, "path", 2_048),
                            out var localImage),
                        localImage)
                        ? null
                        : FormatNamedInput("本地图片", input, "path");
                    break;
                case "audio":
                    value = "[音频]";
                    break;
                case "localAudio":
                    value = FormatNamedInput("本地音频", input, "path");
                    break;
                case "skill":
                    value = FormatNamedInput("技能", input, "name");
                    break;
                case "mention":
                    value = FormatNamedInput("引用", input, "name");
                    break;
                default:
                    value = null;
                    break;
            }
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value);
            }
        }

        return parts.Count == 0 && attachments.Count == 0
            ? null
            : new MappedUserContent(string.Join(Environment.NewLine, parts), attachments);
    }

    private static bool TryAddAttachment(
        ICollection<CodexThreadHistoryAttachmentPayload> attachments,
        bool mapped,
        CodexThreadHistoryAttachmentPayload attachment)
    {
        if (!mapped || attachments.Count >= CodexImageAttachmentMapper.MaxAttachmentsPerMessage)
        {
            return false;
        }

        attachments.Add(attachment);
        return true;
    }

    private static CodexTurnTimingPayload MapTurnTiming(JsonElement turn, string turnId) =>
        new(
            turnId,
            GetOptionalString(turn, "status", 64),
            GetOptionalInt64(turn, "startedAt"),
            GetOptionalInt64(turn, "completedAt"),
            GetOptionalInt64(turn, "durationMs"));

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

    private static long? GetOptionalInt64(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt64(out var number)
            ? number
            : null;
}
