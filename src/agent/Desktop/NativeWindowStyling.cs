using System.Runtime.InteropServices;

namespace CodexControl.Agent.Desktop;

internal static class NativeWindowStyling
{
    private const int DwmWindowAttributeBorderColor = 34;
    private const uint DwmColorNone = 0xFFFFFFFE;

    public static void SuppressSystemBorder(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var borderColor = DwmColorNone;
        _ = DwmSetWindowAttribute(
            windowHandle,
            DwmWindowAttributeBorderColor,
            ref borderColor,
            Marshal.SizeOf<uint>());
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref uint value,
        int valueSize);
}
