using System.Globalization;
using System.Text.Json;

namespace CodexControl.Agent.Codex;

/// <summary>Checks effective scope without changing the shared Thread's permissions.</summary>
public static class ThreadWorkspacePolicy
{
    public static string CodexHome => Path.GetFullPath(Environment.GetEnvironmentVariable("CODEX_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));

    public static bool SamePath(string? path, string expected) => path is not null &&
        Path.IsPathFullyQualified(path) && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)), StringComparison.OrdinalIgnoreCase);

    public static string? VisualizationRoot(string threadId, string codexHome)
    {
        if (!Guid.TryParseExact(threadId, "D", out _)) return null;
        try
        {
            DateTimeOffset created;
            if (threadId[14] == '7')
                created = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(threadId[..8] + threadId[9..13], NumberStyles.HexNumber));
            else if (threadId[14] == '4')
                created = DateTimeOffset.FromUnixTimeSeconds(long.Parse(threadId[..8], NumberStyles.HexNumber));
            else return null;
            return Path.Combine(codexHome, "visualizations", created.UtcDateTime.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)
                .Replace('/', Path.DirectorySeparatorChar), threadId);
        }
        catch (ArgumentException) { return null; }
    }

    public static bool SafeDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        try
        {
            for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public static bool IsAllowedRoot(string path, string workspace, string threadId, string? codexHome = null)
    {
        if (!SafeDirectory(path) || !SafeDirectory(workspace)) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if (SamePath(full, root) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        var artifact = VisualizationRoot(threadId, codexHome ?? CodexHome);
        return artifact is not null && SamePath(full, artifact);
    }

    public static bool IsWithinDirectory(string path, string directory)
    {
        if (!SafeDirectory(path) || !SafeDirectory(directory) || SamePath(directory, Path.GetPathRoot(directory)!)) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return SamePath(full, root) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string? RejectionReason(JsonElement context, string workspace, string threadId,
        bool allowDesktopTemporaryDirectories = false)
    {
        try
        {
            if (!SafeDirectory(workspace) || SamePath(workspace, Path.GetPathRoot(workspace)!)) return "PROJECT_SCOPE_INVALID";
            if (!SamePath(context.GetProperty("cwd").GetString(), workspace)) return "PROJECT_SCOPE_CHANGED";
            var sandbox = context.GetProperty("sandbox");
            if (sandbox.GetProperty("type").GetString() != "workspaceWrite") return "WORKSPACE_WRITE_REQUIRED";
            if (context.GetProperty("approvalPolicy").GetString() is not ("untrusted" or "on-request" or "never") ||
                context.GetProperty("approvalsReviewer").GetString() != "user") return "APPROVAL_POLICY_UNSUPPORTED";
            if (sandbox.GetProperty("networkAccess").GetBoolean())
                return "ADDITIONAL_PERMISSION_REQUIRES_REVIEW";
            if (!sandbox.GetProperty("excludeTmpdirEnvVar").GetBoolean() || !sandbox.GetProperty("excludeSlashTmp").GetBoolean())
            {
                // This requires an explicit local user's grant for an already verified shared-service
                // temp policy. Our own process environment is only an additional conservative check;
                // it is NOT evidence of the independently launched app-server's environment.
                // Unknown service scope stays ungranted by default. Never grant custom TMPDIR,
                // Unix-wide /tmp, or a broader user/profile directory.
                var expectedTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
                var tmpdir = Environment.GetEnvironmentVariable("TMPDIR");
                if (!allowDesktopTemporaryDirectories || !OperatingSystem.IsWindows() || !SafeDirectory(expectedTemp) ||
                    !SamePath(Path.GetTempPath(), expectedTemp) || (tmpdir is not null && !SamePath(tmpdir, expectedTemp)))
                    return "TEMPORARY_DIRECTORY_REQUIRES_REVIEW";
            }
            foreach (var roots in new[] { sandbox.GetProperty("writableRoots"), context.GetProperty("runtimeWorkspaceRoots") })
            {
                if (roots.ValueKind != JsonValueKind.Array) return "WORKSPACE_ROOTS_UNKNOWN";
                foreach (var item in roots.EnumerateArray())
                    if (item.ValueKind != JsonValueKind.String || !IsAllowedRoot(item.GetString()!, workspace, threadId))
                        return "EXTERNAL_WORKSPACE_REQUIRES_REVIEW";
            }
            if (!context.GetProperty("runtimeWorkspaceRoots").EnumerateArray().Any(item => SamePath(item.GetString(), workspace)))
                return "PROJECT_SCOPE_CHANGED";
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or ArgumentException or IOException)
        { return "EFFECTIVE_POLICY_UNKNOWN"; }
    }
}
