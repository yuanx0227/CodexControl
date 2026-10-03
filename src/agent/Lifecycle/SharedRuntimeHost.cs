using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Configuration;
using CodexControl.Agent.Diagnostics;
using CodexControl.Agent.State;

namespace CodexControl.Agent.Lifecycle;

/// <summary>The same product executable hosts a service independently of Desktop and Agent connections.</summary>
public sealed class SharedRuntimeHost : Form
{
    private readonly AgentOptions _options;
    private readonly Label _status = new() { Dock = DockStyle.Fill, Padding = new Padding(20), AutoSize = false };
    private readonly Button _stop = new() { Text = "确认无客户端与任务后停止服务", Dock = DockStyle.Bottom, Height = 45, Enabled = false };
    private Process? _server;
    private SharedServiceIdentity? _identity;
    private Task? _stdout;
    private Task? _stderr;
    private FileStream? _claim;

    private SharedRuntimeHost(AgentOptions options)
    {
        _options = options;
        Text = "Codex Control · 共享服务宿主";
        Width = 600;
        Height = 220;
        Controls.Add(_status);
        Controls.Add(_stop);
        Shown += async (_, _) => await StartServiceAsync();
        _stop.Click += async (_, _) => await StopWhenIdleAsync();
        FormClosing += (_, args) =>
        {
            if (_server is { HasExited: false })
            {
                args.Cancel = true;
                MessageBox.Show(this, "共享服务仍在运行。关闭网页、Agent 或 Desktop 不会停止任务。请先退出依赖此服务的客户端，再点击停止服务。", Text);
            }
        };
        FormClosed += (_, _) => { _claim?.Dispose(); _server?.Dispose(); };
    }

    public static int Run(AgentOptions options)
    {
        EnsureIndependentProcess();
        Application.EnableVisualStyles();
        Application.Run(new SharedRuntimeHost(options));
        return 0;
    }

    public static void EnsureIndependentProcess()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!IsProcessInJob(Process.GetCurrentProcess().Handle, IntPtr.Zero, out var inJob) || inJob)
            throw new AgentConfigurationException("共享宿主不能位于 Desktop 或其他 Job 内；请从 Windows Explorer 的独立入口启动。");
        var parents = ReadParents();
        var current = Environment.ProcessId;
        var visited = new HashSet<int>();
        while (parents.TryGetValue(current, out current) && current > 0 && visited.Add(current))
        {
            try
            {
                using var ancestor = Process.GetProcessById(current);
                if (IsOfficialDesktopImage(ancestor.MainModule?.FileName))
                    throw new AgentConfigurationException("共享宿主不能由 Desktop 的进程树启动；请从 Windows Explorer 独立启动。");
            }
            catch (ArgumentException) { }
            catch (Win32Exception)
            {
                throw new AgentConfigurationException("无法核实共享宿主的父进程，请从 Windows Explorer 的独立入口启动。");
            }
        }
    }

    private async Task StartServiceAsync()
    {
        _status.Text = "正在验证独立宿主并启动共享服务…";
        try
        {
            var endpoint = _options.SharedEndpoint ?? throw new AgentConfigurationException("缺少共享地址。");
            var manifestPath = _options.SharedManifestPath ?? throw new AgentConfigurationException("缺少身份文件。");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            _claim = new FileStream(manifestPath + ".host.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(manifestPath))
                throw new AgentConfigurationException("身份文件已存在。为保留原服务，请连接它；新实例请使用新的身份文件路径。");
            if (LocalTcpOwnership.Rows().Any(row => row.LocalPort == endpoint.Port && row.State == 2))
                throw new AgentConfigurationException("指定端口已被监听；没有启动第二个服务。");
            if (!Path.IsPathFullyQualified(_options.CodexPath))
                throw new AgentConfigurationException("共享宿主要求明确指定已核实的绝对 --codex-path。");
            var version = await SharedServiceIdentity.ReadVersionAsync(_options.CodexPath, CancellationToken.None);
            var info = new ProcessStartInfo(_options.CodexPath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "app-server", "--listen", endpoint.GetLeftPart(UriPartial.Authority) },
            };
            _server = Process.Start(info) ?? throw new AgentConfigurationException("共享服务未能启动。");
            _stdout = _server.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            _stderr = _server.StandardError.BaseStream.CopyToAsync(Stream.Null);
            _identity = new SharedServiceIdentity(Guid.NewGuid().ToString("N"), endpoint.ToString(),
                _server.Id, _server.StartTime.ToUniversalTime(), _server.MainModule!.FileName,
                version, FileVersionInfo.GetVersionInfo(_server.MainModule.FileName).FileVersion);
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
            while (true)
            {
                if (_server.HasExited) throw new AgentConfigurationException("共享服务在建立监听前已退出。");
                try { _identity.Verify(); break; }
                catch (AgentConfigurationException) when (DateTimeOffset.UtcNow < deadline) { await Task.Delay(100); }
            }
            await using (var output = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                await JsonSerializer.SerializeAsync(output, _identity, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            _status.Text = $"共享服务运行中：{endpoint}\n版本：{version}\n服务 PID：{_server.Id}\n请保留此宿主窗口；Agent 和 Desktop 可以独立连接或退出。";
            _stop.Enabled = true;
        }
        catch (Exception exception)
        {
            _status.Text = exception is AgentConfigurationException ? exception.Message : "共享服务启动失败；未修改现有服务或配置。";
            // If already launched, preserve it; never kill merely because metadata/output handling failed.
            _stop.Enabled = _server is { HasExited: false } && _identity is not null && File.Exists(_options.SharedManifestPath);
        }
    }

    private async Task StopWhenIdleAsync()
    {
        if (_server is null || _identity is null) return;
        _stop.Enabled = false;
        try
        {
            _identity.Verify();
            if (HasOtherConnections(0)) throw new AgentConfigurationException("仍有客户端连接；保留服务，请先正常退出这些客户端。");
            using var log = new AgentLog(_options.LogDirectory);
            await using var bridge = new AppServerBridge(_options with { SharedHost = false }, log, new CodexStateManager());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await bridge.StartAsync(timeout.Token);
            string? cursor = null;
            do
            {
                var list = await bridge.SendRequestAsync("thread/loaded/list", new { cursor, limit = 100 }, TimeSpan.FromSeconds(10), timeout.Token);
                foreach (var entry in list.GetProperty("data").EnumerateArray())
                {
                    var threadId = entry.ValueKind == JsonValueKind.String ? entry.GetString() : entry.GetProperty("id").GetString();
                    var read = await bridge.SendRequestAsync("thread/read", new { threadId, includeTurns = true }, TimeSpan.FromSeconds(10), timeout.Token);
                    var thread = read.GetProperty("thread");
                    var status = thread.GetProperty("status");
                    var state = status.ValueKind == JsonValueKind.String ? status.GetString() : status.GetProperty("type").GetString();
                    if (state != "idle" || thread.GetProperty("turns").EnumerateArray().Any(turn => turn.GetProperty("status").GetString() == "inProgress"))
                        throw new AgentConfigurationException("存在活动或状态未知的 Thread；保留服务。");
                }
                cursor = list.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            } while (cursor is not null);
            if (HasOtherConnections(1)) throw new AgentConfigurationException("检测到其他客户端重新连接；保留服务。");
            await bridge.StopAsync(timeout.Token);
            _identity.Verify();
            if (HasOtherConnections(0)) throw new AgentConfigurationException("仍有连接正在关闭或其他客户端进入；请稍后重试。");
            // This is the sole explicit host stop action, never a client-disconnect side effect.
            _server.Kill(entireProcessTree: false);
            await _server.WaitForExitAsync();
            if (_stdout is not null && _stderr is not null) await Task.WhenAll(_stdout, _stderr);
            _status.Text = "共享服务已停止。原生 Desktop 快捷方式保持不变，可正常打开。";
        }
        catch (Exception exception)
        {
            _status.Text = exception is AgentConfigurationException ? exception.Message : "无法证明服务可安全停止，已保留服务。";
        }
        finally { _stop.Enabled = _server is { HasExited: false }; }
    }

    private bool HasOtherConnections(int ownConnections) => LocalTcpOwnership.Rows()
        .Count(row => row.ProcessId == _identity!.ProcessId && row.LocalPort == _options.SharedEndpoint!.Port && row.State == 5) > ownConnections;

    public static async Task<int> LaunchDesktopAsync(AgentOptions options, CancellationToken cancellationToken)
    {
        var path = options.DesktopPath;
        if (!IsOfficialDesktopImage(path) || !File.Exists(path))
            throw new AgentConfigurationException("共享 Desktop 入口仅接受官方 WindowsApps OpenAI.Codex 包内 ChatGPT.exe。");
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                try
                {
                    if (IsOfficialDesktopImage(process.MainModule?.FileName))
                        throw new AgentConfigurationException("官方 Desktop 仍在运行。请先正常退出窗口和托盘，再运行共享入口；不会自动关闭现有任务。");
                }
                catch (Win32Exception) { throw new AgentConfigurationException("无法核实已运行 Desktop，拒绝重复启动。"); }
            }
        }
        var identity = await SharedServiceIdentity.ReadAndVerifyAsync(options.SharedManifestPath!, options.SharedEndpoint!, cancellationToken);
        identity.Verify();
        var info = new ProcessStartInfo(path!) { UseShellExecute = false };
        info.Environment["CODEX_APP_SERVER_WS_URL"] = options.SharedEndpoint!.GetLeftPart(UriPartial.Authority);
        using var launched = Process.Start(info) ?? throw new AgentConfigurationException("官方 Desktop 未能启动。");
        return 0;
    }

    private static bool IsOfficialDesktopImage(string? path)
    {
        if (path is null || !Path.IsPathFullyQualified(path)) return false;
        var package = Directory.GetParent(path)?.Parent;
        var expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        return string.Equals(Path.GetFileName(path), "ChatGPT.exe", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Directory.GetParent(path)?.Name, "app", StringComparison.OrdinalIgnoreCase) &&
            package?.Name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) == true &&
            string.Equals(package.Parent?.FullName, expectedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<int, int> ReadParents()
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new Dictionary<int, int>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
            do { result[(int)entry.ProcessId] = (int)entry.ParentProcessId; }
            while (Process32Next(snapshot, ref entry));
            return result;
        }
        finally { CloseHandle(snapshot); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeap;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Image;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
