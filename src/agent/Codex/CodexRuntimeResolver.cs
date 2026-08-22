using System.Diagnostics;
using CodexControl.Agent.Diagnostics;

namespace CodexControl.Agent.Codex;

public enum CodexRuntimeSource
{
    Configured,
    LocatedExecutable,
    DesktopBundle,
}

public sealed record CodexRuntimeResolution(
    string ExecutablePath,
    CodexRuntimeSource Source,
    bool WasStaged,
    string? DesktopPackageName);

/// <summary>
/// 解析独立 CLI；若 PATH 指向受 WindowsApps ACL 限制的 Codex Desktop 内置运行时，
/// 则将同版本的固定运行时文件原子暂存到当前用户数据目录后再启动。
/// </summary>
public static class CodexRuntimeResolver
{
    private static readonly TimeSpan LocatorTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] DesktopRuntimeFiles =
    [
        "codex.exe",
        "codex-code-mode-host.exe",
        "codex-command-runner.exe",
        "codex-windows-sandbox-setup.exe",
    ];

    public static async Task<CodexRuntimeResolution> ResolveAsync(
        string configuredPath,
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var candidate = configuredPath.Trim();
        var source = CodexRuntimeSource.Configured;
        if (OperatingSystem.IsWindows() && IsCommandName(candidate))
        {
            var located = await LocateWindowsExecutableAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (located is not null)
            {
                candidate = located;
                source = CodexRuntimeSource.LocatedExecutable;
            }
        }

        if (OperatingSystem.IsWindows() && IsWindowsAppsDesktopExecutable(candidate))
        {
            var staged = await StageDesktopRuntimeAsync(candidate, dataDirectory, cancellationToken)
                .ConfigureAwait(false);
            return new CodexRuntimeResolution(
                staged.ExecutablePath,
                CodexRuntimeSource.DesktopBundle,
                staged.WasStaged,
                staged.PackageName);
        }

        return new CodexRuntimeResolution(candidate, source, WasStaged: false, DesktopPackageName: null);
    }

    public static async Task<DesktopRuntimeStageResult> StageDesktopRuntimeAsync(
        string sourceExecutable,
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        var executablePath = Path.GetFullPath(sourceExecutable);
        if (!string.Equals(Path.GetFileName(executablePath), "codex.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentException(
                AgentErrorCodes.CodexNotExecutable,
                $"Codex Desktop 运行时入口必须命名为 codex.exe：{executablePath}");
        }

        var sourceDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new AgentException(AgentErrorCodes.CodexNotExecutable, "Codex Desktop 运行时目录无效。");
        var packageName = FindDesktopPackageName(sourceDirectory)
            ?? throw new AgentException(
                AgentErrorCodes.CodexNotExecutable,
                $"无法识别 Codex Desktop 包版本：{executablePath}");
        ValidateSourceBundle(sourceDirectory);

        var fullDataDirectory = Path.GetFullPath(dataDirectory);
        var runtimeRoot = Path.GetFullPath(Path.Combine(
            fullDataDirectory,
            "codex-runtimes",
            "desktop"));
        EnsureChildPath(fullDataDirectory, runtimeRoot);
        Directory.CreateDirectory(runtimeRoot);

        var safePackageName = SanitizeDirectoryName(packageName);
        var targetDirectory = Path.GetFullPath(Path.Combine(runtimeRoot, safePackageName));
        EnsureChildPath(runtimeRoot, targetDirectory);
        if (RuntimeMatches(sourceDirectory, targetDirectory))
        {
            return new DesktopRuntimeStageResult(
                Path.Combine(targetDirectory, "codex.exe"),
                packageName,
                WasStaged: false);
        }

        if (Directory.Exists(targetDirectory))
        {
            throw new AgentException(
                AgentErrorCodes.CodexNotExecutable,
                $"已暂存的 Codex Desktop 运行时不完整，请删除后重试：{targetDirectory}");
        }

        var stagingDirectory = Path.GetFullPath(Path.Combine(
            runtimeRoot,
            string.Concat(safePackageName, ".staging-", Guid.NewGuid().ToString("N"))));
        EnsureChildPath(runtimeRoot, stagingDirectory);
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            foreach (var fileName in DesktopRuntimeFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CopyFileAsync(
                    Path.Combine(sourceDirectory, fileName),
                    Path.Combine(stagingDirectory, fileName),
                    cancellationToken).ConfigureAwait(false);
            }

            if (!RuntimeMatches(sourceDirectory, stagingDirectory))
            {
                throw new AgentException(
                    AgentErrorCodes.CodexNotExecutable,
                    "Codex Desktop 运行时暂存校验失败。");
            }

            try
            {
                Directory.Move(stagingDirectory, targetDirectory);
            }
            catch (IOException) when (RuntimeMatches(sourceDirectory, targetDirectory))
            {
                // 另一个并发启动已完成同版本的原子暂存，直接复用其结果。
            }

            return new DesktopRuntimeStageResult(
                Path.Combine(targetDirectory, "codex.exe"),
                packageName,
                WasStaged: true);
        }
        catch (AgentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentException(
                AgentErrorCodes.CodexNotExecutable,
                $"无法暂存 Codex Desktop 运行时：{exception.Message}",
                exception);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private static bool IsCommandName(string value) =>
        !Path.IsPathRooted(value) &&
        !value.Contains(Path.DirectorySeparatorChar) &&
        !value.Contains(Path.AltDirectorySeparatorChar);

    private static async Task<string?> LocateWindowsExecutableAsync(
        string command,
        CancellationToken cancellationToken)
    {
        var searchName = Path.HasExtension(command) ? command : string.Concat(command, ".exe");
        var wherePath = Path.Combine(Environment.SystemDirectory, "where.exe");
        if (!File.Exists(wherePath))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = wherePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(searchName);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(LocatorTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            return null;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            return null;
        }

        var candidates = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return candidates.FirstOrDefault(static value => !IsWindowsAppsDesktopExecutable(value))
            ?? candidates.FirstOrDefault();
    }

    private static bool IsWindowsAppsDesktopExecutable(string value)
    {
        if (!Path.IsPathRooted(value))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(value);
        var segments = fullPath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return string.Equals(Path.GetFileName(fullPath), "codex.exe", StringComparison.OrdinalIgnoreCase) &&
               segments.Any(static segment => string.Equals(
                   segment,
                   "WindowsApps",
                   StringComparison.OrdinalIgnoreCase)) &&
               segments.Any(static segment => segment.StartsWith(
                   "OpenAI.Codex_",
                   StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindDesktopPackageName(string sourceDirectory)
    {
        var current = new DirectoryInfo(sourceDirectory);
        while (current is not null)
        {
            if (current.Name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
            {
                return current.Name;
            }

            current = current.Parent;
        }

        return null;
    }

    private static void ValidateSourceBundle(string sourceDirectory)
    {
        foreach (var fileName in DesktopRuntimeFiles)
        {
            var path = Path.Combine(sourceDirectory, fileName);
            if (!File.Exists(path))
            {
                throw new AgentException(
                    AgentErrorCodes.CodexNotExecutable,
                    $"Codex Desktop 运行时缺少文件：{fileName}");
            }
        }
    }

    private static bool RuntimeMatches(string sourceDirectory, string targetDirectory)
    {
        if (!Directory.Exists(targetDirectory))
        {
            return false;
        }

        foreach (var fileName in DesktopRuntimeFiles)
        {
            var source = new FileInfo(Path.Combine(sourceDirectory, fileName));
            var target = new FileInfo(Path.Combine(targetDirectory, fileName));
            if (!source.Exists || !target.Exists || source.Length != target.Length)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string SanitizeDirectoryName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static void EnsureChildPath(string parent, string child)
    {
        var normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) +
                               Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(child).StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentException(
                AgentErrorCodes.CodexNotExecutable,
                $"Codex 运行时暂存路径超出数据目录：{child}");
        }
    }
}

public sealed record DesktopRuntimeStageResult(
    string ExecutablePath,
    string PackageName,
    bool WasStaged);
