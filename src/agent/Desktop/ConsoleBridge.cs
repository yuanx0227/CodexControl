using System.Runtime.InteropServices;

namespace CodexControl.Agent.Desktop;

internal static class ConsoleBridge
{
    private const uint AttachParentProcess = 0xffffffff;

    public static void AttachToParent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _ = AttachConsole(AttachParentProcess);
        var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        Console.SetOut(TextWriter.Synchronized(output));
        Console.SetError(TextWriter.Synchronized(error));
        try
        {
            Console.SetIn(new StreamReader(Console.OpenStandardInput()));
        }
        catch (IOException)
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
