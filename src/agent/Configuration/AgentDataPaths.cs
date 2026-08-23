namespace CodexControl.Agent.Configuration;

public sealed record AgentDataPaths(
    string InstallDirectory,
    string DataDirectory,
    string SettingsPath,
    string LastGoodSettingsPath,
    string StatePath,
    string IdentityPath,
    string LogDirectory,
    string RuntimeDirectory)
{
    public static AgentDataPaths FromApplicationDirectory(string? applicationDirectory = null)
    {
        var installDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        var dataDirectory = Path.GetFullPath(Path.Combine(installDirectory, "data"));
        return FromDataDirectory(dataDirectory, installDirectory);
    }

    public static AgentDataPaths FromDataDirectory(string dataDirectory, string? installDirectory = null)
    {
        var fullDataDirectory = Path.GetFullPath(dataDirectory);
        var fullInstallDirectory = Path.GetFullPath(
            installDirectory ?? Path.GetDirectoryName(fullDataDirectory) ?? AppContext.BaseDirectory);
        return new AgentDataPaths(
            fullInstallDirectory,
            fullDataDirectory,
            Path.Combine(fullDataDirectory, "settings.json"),
            Path.Combine(fullDataDirectory, "settings.last-good.json"),
            Path.Combine(fullDataDirectory, "agent-state.json"),
            Path.Combine(fullDataDirectory, "device-identity.json"),
            Path.Combine(fullDataDirectory, "logs"),
            Path.Combine(fullDataDirectory, "codex-runtimes"));
    }

    public void EnsureWritable()
    {
        Directory.CreateDirectory(DataDirectory);
        var probePath = Path.Combine(DataDirectory, $".write-probe-{Guid.NewGuid():N}");
        try
        {
            using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            stream.WriteByte(0);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentConfigurationException($"数据目录不可写：{DataDirectory}。{exception.Message}");
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
    }
}
