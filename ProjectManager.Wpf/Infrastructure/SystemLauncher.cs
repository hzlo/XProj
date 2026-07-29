using System.ComponentModel;
using System.Diagnostics;

namespace ProjectManager.Wpf.Infrastructure;

public sealed class SystemLauncher
{
    public void OpenFolder(string path)
    {
        EnsureDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = false,
            ArgumentList = { path }
        });
    }

    public void OpenTerminal(string path)
    {
        EnsureDirectory(path);
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe")
            {
                UseShellExecute = false,
                ArgumentList = { "-d", path }
            });
        }
        catch (Win32Exception)
        {
            var shellPath = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            Process.Start(new ProcessStartInfo(shellPath)
            {
                WorkingDirectory = path,
                UseShellExecute = true,
                Arguments = "/K"
            });
        }
    }

    public void OpenInEditor(string path)
    {
        EnsureDirectory(path);
        try
        {
            Process.Start(new ProcessStartInfo("code")
            {
                UseShellExecute = true,
                ArgumentList = { path }
            });
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("未找到 VS Code。请确认 code 命令已加入 PATH。", exception);
        }
    }

    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("无法打开无效的更新地址。");
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true
        });
    }

    private static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"工作目录不存在：{path}");
        }
    }
}
