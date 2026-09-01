using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace XProj.Plugin.Wsl;

public sealed class WslService
{
    public const string ExitMarkerPrefix = "__XPROJ_EXIT__:";

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public bool IsAvailable => File.Exists(Path.Combine(Environment.SystemDirectory, "wsl.exe"));

    public async Task<IReadOnlyList<WslDistribution>> ListDistributionsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return Array.Empty<WslDistribution>();
        }

        using var process = CreateProcess(new[] { "--list", "--verbose" }, Encoding.Unicode);
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"读取 WSL 发行版失败（代码 {process.ExitCode}）。"
                : error.Replace("\0", string.Empty).Trim());
        }

        return ParseDistributions(output);
    }

    public async Task TerminateDistributionAsync(string distribution, CancellationToken cancellationToken = default)
    {
        using var process = CreateProcess(new[] { "--terminate", distribution }, Encoding.Unicode);
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"停止 WSL 发行版失败（代码 {process.ExitCode}）。"
                : error.Replace("\0", string.Empty).Trim());
        }
    }

    /// <summary>
    /// 将发行版导出为 tar 归档。wsl.exe 不提供进度输出，仅返回完成或失败。
    /// </summary>
    public async Task ExportDistributionAsync(string distribution, string filePath, CancellationToken cancellationToken = default)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        await RunManagementCommandAsync(
            new[] { "--export", distribution, filePath },
            $"导出 WSL 发行版失败（代码 {{0}}）。",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ImportDistributionAsync(string distribution, string installLocation, string filePath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(installLocation);
        await RunManagementCommandAsync(
            new[] { "--import", distribution, installLocation, filePath, "--version", "2" },
            $"导入 WSL 发行版失败（代码 {{0}}）。",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task UnregisterDistributionAsync(string distribution, CancellationToken cancellationToken = default)
    {
        await RunManagementCommandAsync(
            new[] { "--unregister", distribution },
            $"卸载 WSL 发行版失败（代码 {{0}}）。",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetDefaultDistributionAsync(string distribution, CancellationToken cancellationToken = default)
    {
        await RunManagementCommandAsync(
            new[] { "--set-default", distribution },
            $"设置默认发行版失败（代码 {{0}}）。",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunManagementCommandAsync(string[] arguments, string failureMessage, CancellationToken cancellationToken)
    {
        using var process = CreateProcess(arguments, Encoding.Unicode);
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (SystemException)
            {
            }

            throw;
        }

        await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, failureMessage, process.ExitCode)
                : error.Replace("\0", string.Empty).Trim());
        }
    }

    public void OpenTerminal(string distribution)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "wt.exe",
                UseShellExecute = false,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            startInfo.ArgumentList.Add("new-tab");
            startInfo.ArgumentList.Add(Path.Combine(Environment.SystemDirectory, "wsl.exe"));
            startInfo.ArgumentList.Add("--distribution");
            startInfo.ArgumentList.Add(distribution);
            if (Process.Start(startInfo) is null)
            {
                StartNativeConsole(distribution);
            }
        }
        catch (Win32Exception)
        {
            StartNativeConsole(distribution);
        }
    }

    private static void StartNativeConsole(string distribution)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "wsl.exe"),
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        startInfo.ArgumentList.Add("--distribution");
        startInfo.ArgumentList.Add(distribution);
        Process.Start(startInfo);
    }

    /// <summary>
    /// 启动一个持久的交互式 bash 会话。进程在命令执行完毕后不会退出，
    /// 后续命令继续写入同一个 shell，环境变量与工作目录状态得以保留。
    /// </summary>
    public WslCommandSession StartShell(string distribution)
    {
        var process = CreateProcess(
            new[] { "--distribution", distribution, "--exec", "bash", "--noprofile", "--norc" },
            Utf8);
        process.Start();
        return new WslCommandSession(process);
    }

    public static string FormatCommand(string command) =>
        $"{command}\necho {ExitMarkerPrefix}$?\n";

    private static Process CreateProcess(IEnumerable<string> arguments, Encoding outputEncoding)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "wsl.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = outputEncoding,
                StandardErrorEncoding = outputEncoding
            },
            EnableRaisingEvents = true
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        return process;
    }

    private static IReadOnlyList<WslDistribution> ParseDistributions(string output)
    {
        var distributions = new List<WslDistribution>();
        foreach (var rawLine in output.Replace("\0", string.Empty).ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 ||
                line.StartsWith("NAME", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Windows Subsystem", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isDefault = line[0] == '*';
            if (isDefault)
            {
                line = line[1..].TrimStart();
            }

            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 3 || !int.TryParse(columns[^1], out var version))
            {
                continue;
            }

            var status = columns[^2];
            var name = string.Join(' ', columns[..^2]);
            distributions.Add(new WslDistribution(
                name,
                version,
                isDefault,
                status.Equals("Running", StringComparison.OrdinalIgnoreCase)));
        }

        return distributions;
    }
}
