using System.Globalization;

namespace CodexControl.Agent.Configuration;

/// <summary>
/// 独立 Agent 的启动参数。这里只保存本地控制面参数，不接管 Codex 的身份、模型或 Endpoint 配置。
/// </summary>
public sealed record AgentOptions(
    string CodexPath,
    int Port,
    string LogDirectory,
    int MaxMessageBytes,
    Uri? RelayUrl,
    string DeviceName,
    string DataDirectory,
    bool CreatePairing,
    bool AllowInsecureRelay,
    bool ProbeOnly,
    bool ShowHelp)
{
    public const int DefaultPort = 8765;
    public const int DefaultMaxMessageBytes = 8 * 1024 * 1024;

    public Uri? SharedEndpoint { get; init; }
    public string? SharedManifestPath { get; init; }
    public IReadOnlyList<string> SharedProjectRoots { get; init; } = [];
    public bool SharedAllowStandardTemporaryDirectories { get; init; }
    public bool SharedHost { get; init; }
    public bool SharedDesktop { get; init; }
    public string? DesktopPath { get; init; }

    public static AgentOptions Parse(IReadOnlyList<string> args)
    {
        var paths = AgentDataPaths.FromApplicationDirectory();
        var codexPath = Environment.GetEnvironmentVariable("CODEX_CONTROL_CODEX_PATH") ?? "codex";
        var port = DefaultPort;
        var logDirectory = paths.LogDirectory;
        var maxMessageBytes = DefaultMaxMessageBytes;
        var relayUrlText = Environment.GetEnvironmentVariable("CODEX_CONTROL_RELAY_URL");
        var deviceName = Environment.MachineName;
        var dataDirectory = paths.DataDirectory;
        var createPairing = false;
        var allowInsecureRelay = false;
        var probeOnly = false;
        var showHelp = false;
        var dataDirectoryOverridden = false;
        var logDirectoryOverridden = false;
        string? sharedEndpoint = null;
        string? sharedManifest = null;
        string? desktopPath = null;
        var sharedHost = false;
        var sharedDesktop = false;
        var sharedProjectRoots = new List<string>();
        var sharedAllowStandardTemporaryDirectories = false;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--shared-endpoint":
                    sharedEndpoint = ReadValue(args, ref index, "--shared-endpoint");
                    break;
                case "--shared-manifest":
                    sharedManifest = ReadValue(args, ref index, "--shared-manifest");
                    break;
                case "--shared-project-root":
                    sharedProjectRoots.Add(ReadValue(args, ref index, "--shared-project-root"));
                    break;
                case "--shared-allow-standard-temp":
                    sharedAllowStandardTemporaryDirectories = true;
                    break;
                case "--shared-host":
                    sharedHost = true;
                    break;
                case "--shared-desktop":
                    sharedDesktop = true;
                    break;
                case "--desktop-path":
                    desktopPath = ReadValue(args, ref index, "--desktop-path");
                    break;
                case "--codex-path":
                    codexPath = ReadValue(args, ref index, "--codex-path");
                    break;
                case "--port":
                    port = ParseInteger(
                        ReadValue(args, ref index, "--port"),
                        "--port",
                        minimum: 1,
                        maximum: 65535);
                    break;
                case "--log-dir":
                    logDirectory = ReadValue(args, ref index, "--log-dir");
                    logDirectoryOverridden = true;
                    break;
                case "--max-message-bytes":
                    maxMessageBytes = ParseInteger(
                        ReadValue(args, ref index, "--max-message-bytes"),
                        "--max-message-bytes",
                        minimum: 64 * 1024,
                        maximum: 128 * 1024 * 1024);
                    break;
                case "--relay-url":
                    relayUrlText = ReadValue(args, ref index, "--relay-url");
                    break;
                case "--device-name":
                    deviceName = ReadValue(args, ref index, "--device-name");
                    break;
                case "--data-dir":
                    dataDirectory = ReadValue(args, ref index, "--data-dir");
                    dataDirectoryOverridden = true;
                    break;
                case "--pair":
                    createPairing = true;
                    break;
                case "--allow-insecure-relay":
                    allowInsecureRelay = true;
                    break;
                case "--probe-only":
                    probeOnly = true;
                    break;
                case "-h":
                case "--help":
                    showHelp = true;
                    break;
                default:
                    throw new AgentConfigurationException($"未知参数：{args[index]}");
            }
        }

        if (string.IsNullOrWhiteSpace(codexPath))
        {
            throw new AgentConfigurationException("Codex 可执行路径不能为空。");
        }

        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new AgentConfigurationException("日志目录不能为空。");
        }

        if (string.IsNullOrWhiteSpace(deviceName) || deviceName.Length > 200)
        {
            throw new AgentConfigurationException("设备名称不能为空且不能超过 200 个字符。");
        }

        Uri? relayUrl = null;
        if (!string.IsNullOrWhiteSpace(relayUrlText))
        {
            if (!Uri.TryCreate(relayUrlText, UriKind.Absolute, out relayUrl) ||
                relayUrl.Scheme is not ("wss" or "https" or "ws" or "http"))
            {
                throw new AgentConfigurationException("Relay URL 必须是绝对 wss/https URL。");
            }

            if (relayUrl.Scheme is "ws" or "http" && !allowInsecureRelay)
            {
                throw new AgentConfigurationException(
                    "非 TLS Relay 只允许在显式指定 --allow-insecure-relay 时用于本地开发。");
            }
        }

        if (createPairing && relayUrl is null)
        {
            throw new AgentConfigurationException("--pair 必须与 --relay-url 一起使用。");
        }

        if (dataDirectoryOverridden && !logDirectoryOverridden)
        {
            logDirectory = Path.Combine(dataDirectory, "logs");
        }

        if ((sharedEndpoint is null) != (sharedManifest is null) ||
            (sharedHost || sharedDesktop) && sharedEndpoint is null || sharedHost && sharedDesktop)
        {
            throw new AgentConfigurationException("共享模式必须同时指定 --shared-endpoint 和 --shared-manifest；host 与 desktop 模式不能同时使用。");
        }
        var validatedEndpoint = sharedEndpoint is null ? null : ParseSharedEndpoint(sharedEndpoint);
        if (sharedDesktop && (string.IsNullOrWhiteSpace(desktopPath) || !Path.IsPathFullyQualified(desktopPath)))
        {
            throw new AgentConfigurationException("--shared-desktop 必须指定官方 Desktop 的绝对 --desktop-path。");
        }

        return new AgentOptions(
            codexPath.Trim(),
            port,
            Path.GetFullPath(logDirectory),
            maxMessageBytes,
            relayUrl,
            deviceName.Trim(),
            Path.GetFullPath(dataDirectory),
            createPairing,
            allowInsecureRelay,
            probeOnly,
            showHelp)
        {
            SharedEndpoint = validatedEndpoint,
            SharedManifestPath = sharedManifest is null ? null : Path.GetFullPath(sharedManifest),
            SharedProjectRoots = SharedProjectAuthorization.Validate(sharedProjectRoots),
            SharedAllowStandardTemporaryDirectories = sharedAllowStandardTemporaryDirectories,
            SharedHost = sharedHost,
            SharedDesktop = sharedDesktop,
            DesktopPath = desktopPath,
        };
    }

    public static AgentOptions FromSettings(AgentSettings settings, AgentDataPaths paths)
    {
        var validated = settings.Validate();
        var relayUrl = string.IsNullOrWhiteSpace(validated.RelayRootUrl)
            ? null
            : new Uri(validated.RelayRootUrl, UriKind.Absolute);
        var codexPath = validated.CodexPathMode == CodexPathMode.Manual
            ? validated.CodexPath!
            : Environment.GetEnvironmentVariable("CODEX_CONTROL_CODEX_PATH") ?? "codex";
        var port = validated.LocalPortMode == LocalPortMode.Fixed
            ? validated.FixedPort!.Value
            : 0;
        return new AgentOptions(
            codexPath,
            port,
            paths.LogDirectory,
            DefaultMaxMessageBytes,
            relayUrl,
            validated.DeviceName,
            paths.DataDirectory,
            CreatePairing: false,
            AllowInsecureRelay: relayUrl?.Scheme == Uri.UriSchemeHttp,
            ProbeOnly: false,
            ShowHelp: false)
        {
            SharedEndpoint = validated.SharedEndpoint is null ? null : ParseSharedEndpoint(validated.SharedEndpoint),
            SharedManifestPath = validated.SharedManifestPath,
            SharedProjectRoots = validated.SharedProjectRoots,
            SharedAllowStandardTemporaryDirectories = validated.SharedAllowStandardTemporaryDirectories,
        };
    }

    public static AgentOptions ForTests(string codexPath, string logDirectory, int port = 0) =>
        new(
            codexPath,
            port,
            Path.GetFullPath(logDirectory),
            DefaultMaxMessageBytes,
            RelayUrl: null,
            DeviceName: "Test Device",
            DataDirectory: Path.Combine(Path.GetFullPath(logDirectory), "data"),
            CreatePairing: false,
            AllowInsecureRelay: false,
            ProbeOnly: false,
            ShowHelp: false);

    public static string HelpText => """
        CodexControlAgent

        用法：
          CodexControlAgent.exe                 打开托盘和设置窗口
          CodexControlAgent.exe --background    静默进入托盘
          CodexControlAgent.exe --headless [options]

        参数：
          --codex-path <path>        可执行的 Codex CLI 路径；默认也自动发现 Codex Desktop 运行时
          --port <1-65535>           localhost WebSocket 端口，默认 8765
          --log-dir <path>           rolling log 目录
          --max-message-bytes <n>    单条 WebSocket/JSONL 消息上限，默认 8 MiB
          --relay-url <wss-url>      自托管 Relay 根地址
          --device-name <name>       Relay 中显示的设备名称
          --data-dir <path>          DPAPI 身份和本地状态目录
          --pair                     连接 Relay 后创建并显示三分钟配对码
          --allow-insecure-relay     仅本地开发允许 ws/http Relay
          --headless                 使用命令行模式；参数只影响当前进程
          --background               GUI 模式静默进入托盘
          --shared-endpoint <ws-url>  连接已知共享服务，只接受 ws://127.0.0.1:<port>
          --shared-manifest <path>    本机服务身份文件；连接时核对 PID、创建时间、镜像、版本和监听
          --shared-project-root <dir> 本机明确授权的共享项目目录，可重复；不设置时禁止新增远程工作
          --shared-allow-standard-temp 额外授权 Desktop 标准临时目录策略；需已核实服务环境，无自定义 TMPDIR
          --shared-host              独立运行共享服务宿主；不允许从 Desktop 进程树/Job 启动
          --shared-desktop           以进程级 WS 环境打开官方 Desktop；必须先正常退出所有 Desktop
          --desktop-path <path>      官方 WindowsApps 包内 ChatGPT.exe 的绝对路径
          --probe-only               只验证 Codex CLI/app-server 能力，不启动 Agent
          -h, --help                 显示帮助

        Agent 只监听 127.0.0.1，不读取或上传 OpenAI API Key。
        """;

    public static Uri ParseSharedEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "ws" ||
            uri.Host != "127.0.0.1" || uri.Port is < 1 or > 65535 ||
            uri.AbsolutePath is not ("" or "/") || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new AgentConfigurationException("共享服务地址只允许无凭据、查询和路径的 ws://127.0.0.1:<port>。");
        }
        return uri;
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count)
        {
            throw new AgentConfigurationException($"参数 {option} 缺少值。");
        }

        index++;
        return args[index];
    }

    private static int ParseInteger(
        string value,
        string option,
        int minimum,
        int maximum)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ||
            result < minimum ||
            result > maximum)
        {
            throw new AgentConfigurationException(
                $"参数 {option} 必须是 {minimum.ToString(CultureInfo.InvariantCulture)} 到 " +
                $"{maximum.ToString(CultureInfo.InvariantCulture)} 之间的整数。");
        }

        return result;
    }
}

public sealed class AgentConfigurationException(string message) : Exception(message);
