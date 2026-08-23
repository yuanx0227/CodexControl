using System.Text.Json;
using CodexControl.Protocol;

namespace CodexControl.Agent.Control;

internal static class CodexFileChangeMapper
{
    public const int MaximumChanges = 30;

    public static IReadOnlyList<CodexThreadHistoryFileChangePayload> Map(JsonElement item)
    {
        if (!item.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var mapped = new List<CodexThreadHistoryFileChangePayload>();
        foreach (var change in changes.EnumerateArray())
        {
            if (mapped.Count >= MaximumChanges || change.ValueKind != JsonValueKind.Object ||
                !change.TryGetProperty("path", out var pathValue) || pathValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(pathValue.GetString()))
            {
                continue;
            }

            var path = pathValue.GetString()!;
            if (path.Length > 2_048)
            {
                path = path[..2_048];
            }

            var kind = ReadString(change, "kind", 64);
            var diff = ReadString(change, "diff", 200_000);
            var (additions, deletions) = CountDiffLines(diff);
            mapped.Add(new CodexThreadHistoryFileChangePayload(path, kind, additions, deletions));
        }

        return mapped;
    }

    public static string Format(IReadOnlyList<CodexThreadHistoryFileChangePayload> changes)
    {
        if (changes.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, changes.Select(change =>
        {
            var statistics = FormatStatistics(change.Additions, change.Deletions);
            return string.IsNullOrEmpty(statistics) ? change.Path : $"{change.Path}  {statistics}";
        }));
    }

    private static (int? Additions, int? Deletions) CountDiffLines(string? diff)
    {
        if (diff is null)
        {
            return (null, null);
        }

        var additions = 0;
        var deletions = 0;
        using var reader = new StringReader(diff);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                additions++;
            }
            else if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
            {
                deletions++;
            }
        }

        return (additions, deletions);
    }

    private static string FormatStatistics(int? additions, int? deletions) =>
        additions is null && deletions is null ? string.Empty : $"+{additions ?? 0} -{deletions ?? 0}";

    private static string? ReadString(JsonElement value, string propertyName, int maximumLength)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = property.GetString();
        return text is null || text.Length <= maximumLength ? text : text[..maximumLength];
    }
}
