using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexControl.Agent.Configuration;

namespace CodexControl.Agent.Codex;

/// <summary>Only local, non-secret process metadata. Never a credential or protocol transcript.</summary>
public sealed record SharedServiceIdentity(
    string ServiceInstanceId,
    string Endpoint,
    int ProcessId,
    DateTimeOffset StartedUtc,
    string ImagePath,
    string Version,
    string? FileVersion)
{
    public static async Task<SharedServiceIdentity> ReadAndVerifyAsync(
        string manifestPath, Uri endpoint, CancellationToken cancellationToken)
    {
        AgentOptions.ParseSharedEndpoint(endpoint.ToString());
        if (new FileInfo(manifestPath).Length > 64 * 1024)
            throw new AgentConfigurationException("共享服务身份文件超出允许大小。");
        using var stream = File.OpenRead(manifestPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        SharedServiceIdentity identity;
        if (root.TryGetProperty("server", out var server))
        {
            // The approved read-only trial manifest is also accepted; no mutation/migration needed.
            var pid = server.GetProperty("pid").GetInt32();
            var started = server.GetProperty("startedUtc").GetDateTimeOffset();
            identity = new SharedServiceIdentity(
                $"local-{pid}-{started.UtcTicks}", root.GetProperty("endpoint").GetString()!, pid, started,
                server.GetProperty("image").GetString()!, server.GetProperty("version").GetString()!, null);
        }
        else
        {
            identity = root.Deserialize<SharedServiceIdentity>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new AgentConfigurationException("共享服务身份文件为空。");
        }
        if (!Uri.TryCreate(identity.Endpoint, UriKind.Absolute, out var recorded) ||
            recorded != endpoint || string.IsNullOrWhiteSpace(identity.ServiceInstanceId) ||
            string.IsNullOrWhiteSpace(identity.Version))
            throw new AgentConfigurationException("共享服务地址或实例身份与文件不匹配。");
        identity.Verify();
        // Match the actual executable's CLI version, not Desktop package/PATH CLI versions.
        var version = await ReadVersionAsync(identity.ImagePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(version, identity.Version, StringComparison.Ordinal))
            throw new AgentConfigurationException("共享服务可执行文件版本已变化，需重新验证实例。");
        identity.Verify();
        return identity;
    }

    public void Verify()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("共享服务身份核验只支持 Windows。");
        var endpoint = AgentOptions.ParseSharedEndpoint(Endpoint);
        Process process;
        try { process = Process.GetProcessById(ProcessId); }
        catch (ArgumentException) { throw new AgentConfigurationException("共享服务进程已退出；不会连接其他实例。"); }
        using var processHandle = process;
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != StartedUtc.UtcTicks ||
            !string.Equals(process.MainModule?.FileName, ImagePath, StringComparison.OrdinalIgnoreCase) ||
            FileVersion is not null && !string.Equals(FileVersionInfo.GetVersionInfo(ImagePath).FileVersion, FileVersion, StringComparison.Ordinal) ||
            !LocalTcpOwnership.Rows().Any(row => row.LocalAddress.Equals(IPAddress.Loopback) &&
                row.LocalPort == endpoint.Port && row.ProcessId == ProcessId && row.State == 2))
            throw new AgentConfigurationException("共享服务 PID、创建时间、镜像或 127.0.0.1 监听归属已变化；拒绝连接其他实例。");
    }

    public static async Task<string> ReadVersionAsync(string imagePath, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(imagePath) || !File.Exists(imagePath))
            throw new AgentConfigurationException("共享服务镜像必须是存在的绝对路径。");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(imagePath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "--version" },
            },
        };
        if (!process.Start()) throw new AgentConfigurationException("无法读取共享服务镜像版本。");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var discard = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var value = (await output.ConfigureAwait(false)).Trim();
            await discard.ConfigureAwait(false);
            if (process.ExitCode != 0 || value.Length > 200 || !value.StartsWith("codex-cli ", StringComparison.Ordinal) || value.Contains('\n'))
                throw new AgentConfigurationException("无法确认共享服务的 codex-cli 版本。");
            return value;
        }
        finally
        {
            if (!process.HasExited) process.Kill(); // Only the --version child created by this call.
        }
    }
}

/// <summary>Reads the Windows TCP ownership table, without scanning or connecting to any port.</summary>
public static class LocalTcpOwnership
{
    public sealed record Row(IPAddress LocalAddress, int LocalPort, int RemotePort, int ProcessId, int State);

    public static IReadOnlyList<Row> Rows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0);
            if (result != 0) throw new Win32Exception((int)result);
            var count = Marshal.ReadInt32(buffer);
            var rows = new List<Row>(count);
            for (var i = 0; i < count; i++)
            {
                var row = IntPtr.Add(buffer, 4 + i * 24);
                rows.Add(new Row(
                    new IPAddress(unchecked((uint)Marshal.ReadInt32(row, 4))),
                    ReadPort(row, 8), ReadPort(row, 16), Marshal.ReadInt32(row, 20), Marshal.ReadInt32(row)));
            }
            return rows;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static int ReadPort(IntPtr row, int offset) => (Marshal.ReadByte(row, offset) << 8) | Marshal.ReadByte(row, offset + 1);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
