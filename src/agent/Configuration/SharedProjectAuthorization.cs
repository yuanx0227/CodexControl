namespace CodexControl.Agent.Configuration;

/// <summary>Authorization is supplied by the local user, never inferred from a remote Thread cwd.</summary>
public static class SharedProjectAuthorization
{
    public static IReadOnlyList<string> Validate(IEnumerable<string>? roots)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in roots ?? [])
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
                throw new AgentConfigurationException("共享项目授权目录必须是存在的绝对目录。");
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (!Directory.Exists(path) || string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!), StringComparison.OrdinalIgnoreCase))
                throw new AgentConfigurationException("共享项目授权目录必须存在且不能是整个卷或共享根目录。");
            try
            {
                for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
                    if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new AgentConfigurationException("共享项目授权目录及其父目录不能包含符号链接或重解析点。");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new AgentConfigurationException("无法核实共享项目授权目录的文件系统属性。");
            }
            result.Add(path);
        }
        return Array.AsReadOnly(result.ToArray());
    }
}
