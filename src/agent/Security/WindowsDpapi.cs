using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodexControl.Agent.Security;

internal static class WindowsDpapi
{
    private const int CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(ReadOnlySpan<byte> value) => Transform(value, protect: true);

    public static byte[] Unprotect(ReadOnlySpan<byte> value) => Transform(value, protect: false);

    private static byte[] Transform(ReadOnlySpan<byte> value, bool protect)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI identity storage requires Windows.");
        }

        var input = CreateBlob(value);
        try
        {
            var success = protect
                ? CryptProtectData(
                    ref input,
                    "Codex Control Device Identity",
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var output)
                : CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output);
            if (!success)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(input.Data);
            }
        }
    }

    private static DataBlob CreateBlob(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return default;
        }

        var pointer = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value.ToArray(), 0, pointer, value.Length);
        return new DataBlob { Length = value.Length, Data = pointer };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
