using System.Text.Json;

namespace CodexControl.SharedSessionProbe;

internal static class TestThreadPolicy
{
    public static bool SameDirectory(string? left, string right) =>
        left is not null && Path.IsPathFullyQualified(left) &&
        string.Equals(NormalizeDirectory(left), NormalizeDirectory(right), PathComparison);

    public static void RequireEmptyWorkspace(string workspace)
    {
        if (!Path.IsPathFullyQualified(workspace) || !Directory.Exists(workspace) ||
            (OperatingSystem.IsWindows() && Path.GetFullPath(workspace).StartsWith(@"\\", StringComparison.Ordinal)) ||
            SameDirectory(workspace, Path.GetPathRoot(workspace)!) ||
            !HasSafeAncestors(workspace) || Directory.EnumerateFileSystemEntries(workspace).Any())
            throw new ProbeRejectedException("EMPTY_ISOLATED_WORKSPACE_REQUIRED");
    }

    public static bool IsIsolatedSandbox(JsonElement response, string workspace, string? threadId = null)
    {
        // Observation allows only the official artifact directory for this exact Thread.
        // Creation continues to require the original empty, single-workspace scope.
        if (threadId is not null)
            return CodexControl.Agent.Codex.ThreadWorkspacePolicy.RejectionReason(response, workspace, threadId) is null;
        var sandbox = Program.At(response, "sandbox");
        if (Program.Text(sandbox, "type") != "workspaceWrite" ||
            Program.At(sandbox, "networkAccess").ValueKind != JsonValueKind.False ||
            Program.At(sandbox, "excludeTmpdirEnvVar").ValueKind != JsonValueKind.True ||
            Program.At(sandbox, "excludeSlashTmp").ValueKind != JsonValueKind.True)
            return false;

        return IsWithinWorkspace(workspace, workspace) &&
               RootsStayWithinWorkspace(Program.At(sandbox, "writableRoots"), workspace) &&
               RootsStayWithinWorkspace(Program.At(response, "runtimeWorkspaceRoots"), workspace);
    }

    public static bool HasExactWorkspaceRoots(JsonElement response, string workspace) =>
        SameDirectory(Program.Text(response, "cwd"), workspace) &&
        ExactlyWorkspace(Program.At(response, "runtimeWorkspaceRoots"), workspace) &&
        AdditionalRootsAreWorkspace(Program.At(response, "sandbox", "writableRoots"), workspace);

    private static bool AdditionalRootsAreWorkspace(JsonElement roots, string workspace) =>
        // WorkspaceWrite always includes cwd. Its serialized roots are additional roots,
        // so [] and repetitions of the verified cwd describe the same effective write scope.
        roots.ValueKind == JsonValueKind.Array && roots.EnumerateArray().All(root =>
            root.ValueKind == JsonValueKind.String && SameDirectory(root.GetString(), workspace));

    private static bool ExactlyWorkspace(JsonElement roots, string workspace) =>
        roots.ValueKind == JsonValueKind.Array && roots.GetArrayLength() == 1 &&
        roots[0].ValueKind == JsonValueKind.String && SameDirectory(roots[0].GetString(), workspace);

    private static bool RootsStayWithinWorkspace(JsonElement roots, string workspace) =>
        roots.ValueKind == JsonValueKind.Array && roots.EnumerateArray().All(root =>
            root.ValueKind == JsonValueKind.String && IsWithinWorkspace(root.GetString()!, workspace));

    private static bool IsWithinWorkspace(string candidate, string workspace)
    {
        if (!Path.IsPathFullyQualified(candidate)) return false;
        var path = NormalizeDirectory(candidate);
        var root = NormalizeDirectory(workspace);
        return (string.Equals(path, root, PathComparison) ||
                path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison)) &&
               HasSafeAncestors(path);
    }

    private static bool HasSafeAncestors(string path)
    {
        try
        {
            // Include ancestors above the workspace: a parent junction can redirect the entire test directory.
            for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
            {
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeDirectory(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
