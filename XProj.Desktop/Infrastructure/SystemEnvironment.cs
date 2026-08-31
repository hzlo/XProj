using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ProjectManager.Wpf.Infrastructure;

internal static class SystemEnvironment
{
    public static void Refresh(ProcessStartInfo startInfo)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        if (!CreateEnvironmentBlock(out var environmentBlock, identity.AccessToken, inherit: true))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法读取最新的 Windows 环境变量。");
        }

        try
        {
            var variables = ReadEnvironmentBlock(environmentBlock);
            startInfo.Environment.Clear();
            foreach (var (name, value) in variables)
            {
                startInfo.Environment[name] = value;
            }
        }
        finally
        {
            DestroyEnvironmentBlock(environmentBlock);
        }
    }

    private static Dictionary<string, string> ReadEnvironmentBlock(IntPtr environmentBlock)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var current = environmentBlock;
        while (true)
        {
            var entry = Marshal.PtrToStringUni(current);
            if (string.IsNullOrEmpty(entry))
            {
                return variables;
            }

            current += (entry.Length + 1) * sizeof(char);
            var separatorIndex = entry.IndexOf('=');
            if (separatorIndex > 0)
            {
                variables[entry[..separatorIndex]] = entry[(separatorIndex + 1)..];
            }
        }
    }

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr environmentBlock,
        SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environmentBlock);
}
