using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DnevnikSdelok.Security;

public static class WindowsSecretProtection
{
    private const uint UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(string value)
    {
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(value);
        var input = CreateBlob(plainBytes);
        try
        {
            if (!CryptProtectData(
                ref input,
                "DnevnikSdelok",
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                UiForbidden,
                out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var protectedBytes = new byte[output.Size];
                Marshal.Copy(output.Data, protectedBytes, 0, output.Size);
                return protectedBytes;
            }
            finally
            {
                FreeOutput(ref output, clear: false);
            }
        }
        finally
        {
            FreeBlob(ref input, clear: true);
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    public static string Unprotect(byte[] value)
    {
        var input = CreateBlob(value);
        byte[]? plainBytes = null;
        try
        {
            if (!CryptUnprotectData(
                ref input,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                UiForbidden,
                out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                plainBytes = new byte[output.Size];
                Marshal.Copy(output.Data, plainBytes, 0, output.Size);
                return System.Text.Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                FreeOutput(ref output, clear: true);
            }
        }
        finally
        {
            FreeBlob(ref input, clear: false);
            if (plainBytes is not null)
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = pointer };
    }

    private static void FreeBlob(ref DataBlob blob, bool clear)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        if (clear)
        {
            var zeroes = new byte[blob.Size];
            Marshal.Copy(zeroes, 0, blob.Data, zeroes.Length);
        }
        Marshal.FreeHGlobal(blob.Data);
        blob = default;
    }

    private static void FreeOutput(ref DataBlob blob, bool clear)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        if (clear)
        {
            var zeroes = new byte[blob.Size];
            Marshal.Copy(zeroes, 0, blob.Data, zeroes.Length);
        }
        LocalFree(blob.Data);
        blob = default;
    }
}
