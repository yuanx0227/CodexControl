using System.Text.Json;

namespace CodexControl.Agent.Configuration;

public enum SettingsLoadSource
{
    Defaults,
    Primary,
    LastGood,
}

public sealed record SettingsLoadResult(
    AgentSettings Settings,
    SettingsLoadSource Source,
    string? Warning,
    string? QuarantinedPath);

public sealed class AgentSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly AgentDataPaths _paths;
    private readonly object _gate = new();

    public AgentSettingsStore(AgentDataPaths paths)
    {
        _paths = paths;
    }

    public SettingsLoadResult Load()
    {
        lock (_gate)
        {
            _paths.EnsureWritable();
            if (!File.Exists(_paths.SettingsPath))
            {
                return new SettingsLoadResult(AgentSettings.Default, SettingsLoadSource.Defaults, null, null);
            }

            try
            {
                return new SettingsLoadResult(ReadAndValidate(_paths.SettingsPath), SettingsLoadSource.Primary, null, null);
            }
            catch (Exception primaryException) when (IsRecoverable(primaryException))
            {
                var quarantinedPath = QuarantinePrimary();
                if (File.Exists(_paths.LastGoodSettingsPath))
                {
                    try
                    {
                        var recovered = ReadAndValidate(_paths.LastGoodSettingsPath);
                        return new SettingsLoadResult(
                            recovered,
                            SettingsLoadSource.LastGood,
                            $"设置文件已损坏，已加载上次有效配置：{primaryException.Message}",
                            quarantinedPath);
                    }
                    catch (Exception backupException) when (IsRecoverable(backupException))
                    {
                        return new SettingsLoadResult(
                            AgentSettings.Default,
                            SettingsLoadSource.Defaults,
                            $"设置和备份均不可用：{backupException.Message}",
                            quarantinedPath);
                    }
                }

                return new SettingsLoadResult(
                    AgentSettings.Default,
                    SettingsLoadSource.Defaults,
                    $"设置文件已损坏：{primaryException.Message}",
                    quarantinedPath);
            }
        }
    }

    public AgentSettings Save(AgentSettings settings)
    {
        lock (_gate)
        {
            _paths.EnsureWritable();
            var validated = settings.Validate();
            var temporaryPath = Path.Combine(
                _paths.DataDirectory,
                $"settings.tmp-{Guid.NewGuid():N}.json");
            try
            {
                var json = JsonSerializer.Serialize(validated, JsonOptions);
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           16 * 1024,
                           FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                _ = ReadAndValidate(temporaryPath);
                if (File.Exists(_paths.SettingsPath))
                {
                    File.Copy(_paths.SettingsPath, _paths.LastGoodSettingsPath, overwrite: true);
                    File.Move(temporaryPath, _paths.SettingsPath, overwrite: true);
                }
                else
                {
                    File.Move(temporaryPath, _paths.SettingsPath, overwrite: false);
                    File.Copy(_paths.SettingsPath, _paths.LastGoodSettingsPath, overwrite: true);
                }

                return validated;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    private static AgentSettings ReadAndValidate(string path)
    {
        var json = File.ReadAllText(path);
        var settings = JsonSerializer.Deserialize<AgentSettings>(json, JsonOptions)
            ?? throw new AgentConfigurationException("设置文件为空。");
        return settings.Validate();
    }

    private string QuarantinePrimary()
    {
        var quarantine = Path.Combine(
            _paths.DataDirectory,
            $"settings.invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json");
        try
        {
            File.Move(_paths.SettingsPath, quarantine, overwrite: false);
            return quarantine;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Concat(_paths.SettingsPath, " (无法隔离：", exception.Message, ")");
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is JsonException or IOException or UnauthorizedAccessException or AgentConfigurationException;
}
