using Microsoft.Win32;

namespace CodexControl.Agent.Desktop;

internal static class StartupRegistrar
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexControlAgent";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string value &&
               string.Equals(value, BuildCommand(), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户启动项。");
        if (enabled)
        {
            key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string BuildCommand()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定 Agent 可执行文件路径。");
        return $"\"{executable}\" --background";
    }
}
