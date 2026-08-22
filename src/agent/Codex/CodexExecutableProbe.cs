using System.ComponentModel;
using System.Diagnostics;
using CodexControl.Agent.Diagnostics;

namespace CodexControl.Agent.Codex;

public sealed record CodexCapabilityProbeResult(
    string Executable,
    string Version,
    bool SupportsRemote,
    bool SupportsAppServerStdio);

/// <summary>
/// 只执行帮助和版本命令，不读取用户 Key，不调用模型。
/// </summary>
public static class CodexExecutableProbe
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    public static async Task<CodexCapabilityProbeResult> ProbeAsync(
        string codexPath,
        CancellationToken cancellationToken)
    {
        var version = await RunAsync(codexPath, ["--version"], cancellationToken).ConfigureAwait(false);
        var help = await RunAsync(codexPath, ["--help"], cancellationToken).ConfigureAwait(false);
        var appServerHelp = await RunAsync(
            codexPath,
            ["app-server", "--help"],
            cancellationToken).ConfigureAwait(false);

        var supportsRemote = help.StandardOutput.Contains("--remote", StringComparison.Ordinal);
        var supportsAppServerStdio =
            appServerHelp.StandardOutput.Contains("--listen", StringComparison.Ordinal) &&
            appServerHelp.StandardOutput.Contains("stdio://", StringComparison.Ordinal);

        if (!supportsRemote || !supportsAppServerStdio)
        {
            throw new AgentException(
                AgentErrorCodes.CodexVersionUnsupported,
                "当前 Codex CLI 不同时支持 --remote 与 app-server stdio://。");
        }

        return new CodexCapabilityProbeResult(
            codexPath,
            FirstNonEmptyLine(version.StandardOutput) ?? "unknown",
            supportsRemote,
            supportsAppServerStdio);
    }

    private static async Task<CommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new AgentException(
                    AgentErrorCodes.CodexNotExecutable,
                    "Codex CLI 进程未能启动。");
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            throw new AgentException(
                AgentErrorCodes.CodexNotFound,
                $"找不到 Codex CLI：{executable}",
                exception);
        }
        catch (Win32Exception exception)
        {
            throw new AgentException(
                AgentErrorCodes.CodexNotExecutable,
                $"无法执行 Codex CLI：{executable}",
                exception);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw new AgentException(
                AgentErrorCodes.CodexNotExecutable,
                $"Codex 能力检查超时：{string.Join(' ', arguments)}");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new AgentException(
                AgentErrorCodes.CodexNotExecutable,
                $"Codex 能力检查失败，退出码 {process.ExitCode}：{string.Join(' ', arguments)}；" +
                $"stderr 字节数 {stderr.Length}");
        }

        return new CommandResult(stdout, stderr);
    }

    private static string? FirstNonEmptyLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private sealed record CommandResult(string StandardOutput, string StandardError);
}
