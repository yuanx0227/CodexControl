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

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
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
            showHelp);
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
            ShowHelp: false);
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
          --probe-only               只验证 Codex CLI/app-server 能力，不启动 Agent
          -h, --help                 显示帮助

        Agent 只监听 127.0.0.1，不读取或上传 OpenAI API Key。
        """;

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
