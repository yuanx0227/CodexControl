namespace CodexControl.Agent.Configuration;

public enum CodexPathMode
{
    Auto,
    Manual,
}

public enum LocalPortMode
{
    Auto,
    Fixed,
}

public sealed record AgentSettings(
    int SchemaVersion,
    string DeviceName,
    string? RelayRootUrl,
    bool RemoteAccessPaused,
    bool RunAtLogin,
    bool StartCoreAutomatically,
    CodexPathMode CodexPathMode,
    string? CodexPath,
    LocalPortMode LocalPortMode,
    int? FixedPort,
    string Theme)
{
    public const int CurrentSchemaVersion = 1;
    public string? SharedEndpoint { get; init; }
    public string? SharedManifestPath { get; init; }
    public IReadOnlyList<string> SharedProjectRoots { get; init; } = [];
    public bool SharedAllowStandardTemporaryDirectories { get; init; }

    public static AgentSettings Default { get; } = new(
        CurrentSchemaVersion,
        Environment.MachineName,
        RelayRootUrl: null,
        RemoteAccessPaused: false,
        RunAtLogin: true,
        StartCoreAutomatically: true,
        CodexPathMode.Auto,
        CodexPath: null,
        LocalPortMode.Auto,
        FixedPort: null,
        Theme: "system");

    public bool IsConfigured => !string.IsNullOrWhiteSpace(RelayRootUrl) &&
                                !string.IsNullOrWhiteSpace(DeviceName);

    public AgentSettings Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new AgentConfigurationException(
                $"不支持的设置版本：{SchemaVersion}，当前版本：{CurrentSchemaVersion}。");
        }

        if (string.IsNullOrWhiteSpace(DeviceName) || DeviceName.Trim().Length > 200)
        {
            throw new AgentConfigurationException("电脑名称不能为空且不能超过 200 个字符。");
        }

        var relayRoot = NormalizeRelayRoot(RelayRootUrl);
        if (string.IsNullOrWhiteSpace(SharedEndpoint) != string.IsNullOrWhiteSpace(SharedManifestPath))
        {
            throw new AgentConfigurationException("共享服务地址和身份文件必须同时设置。");
        }
        var sharedEndpoint = string.IsNullOrWhiteSpace(SharedEndpoint)
            ? null : AgentOptions.ParseSharedEndpoint(SharedEndpoint.Trim()).ToString();
        if (CodexPathMode == CodexPathMode.Manual && string.IsNullOrWhiteSpace(CodexPath))
        {
            throw new AgentConfigurationException("手动 Codex 路径不能为空。");
        }

        if (LocalPortMode == LocalPortMode.Fixed &&
            (FixedPort is null or < 1 or > 65535))
        {
            throw new AgentConfigurationException("固定端口必须是 1 到 65535 之间的整数。");
        }

        if (!string.Equals(Theme, "system", StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentConfigurationException("当前版本只支持跟随系统外观。");
        }

        return this with
        {
            DeviceName = DeviceName.Trim(),
            RelayRootUrl = relayRoot,
            CodexPath = string.IsNullOrWhiteSpace(CodexPath) ? null : CodexPath.Trim(),
            FixedPort = LocalPortMode == LocalPortMode.Fixed ? FixedPort : null,
            Theme = "system",
            SharedEndpoint = sharedEndpoint,
            SharedManifestPath = sharedEndpoint is null ? null : Path.GetFullPath(SharedManifestPath!),
            SharedProjectRoots = SharedProjectAuthorization.Validate(SharedProjectRoots),
        };
    }

    public static string? NormalizeRelayRoot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http"))
        {
            throw new AgentConfigurationException("Relay 根地址必须是绝对 https URL。");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath is not ("" or "/"))
        {
            throw new AgentConfigurationException("Relay 根地址不能包含凭据、子路径、查询或片段。");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(uri.Host))
        {
            throw new AgentConfigurationException("非 TLS Relay 只允许 localhost/loopback 开发地址。");
        }

        return new UriBuilder(uri)
        {
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri.ToString().TrimEnd('/');
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
}
