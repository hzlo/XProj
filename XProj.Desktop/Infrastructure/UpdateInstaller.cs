using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;

namespace ProjectManager.Wpf.Infrastructure;

internal sealed class UpdateInstaller
{
    private static readonly HttpClient DownloadHttpClient = CreateHttpClient();

    public async Task<string> DownloadAsync(
        UpdateCheckResult result,
        IProgress<int>? progress,
        CancellationToken cancellationToken = default)
    {
        var assetName = $"XProj-{result.LatestTag}-win-x64-{GetPackageKind()}.zip";
        var downloadUri = new Uri($"https://github.com/hzlo/XProj/releases/download/{result.LatestTag}/{assetName}");
        var stagingDirectory = CreateStagingDirectory(result.LatestTag);
        var packagePath = Path.Combine(stagingDirectory, assetName);

        using var response = await DownloadHttpClient.GetAsync(
            downloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"下载更新包失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(packagePath);

        var buffer = new byte[81920];
        long receivedBytes = 0;
        var lastReportedPercent = -1;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            receivedBytes += read;
            if (totalBytes > 0)
            {
                var percent = (int)(receivedBytes * 100 / totalBytes);
                if (percent != lastReportedPercent)
                {
                    lastReportedPercent = percent;
                    progress?.Report(percent);
                }
            }
        }

        progress?.Report(100);
        return packagePath;
    }

    public void ScheduleApplyAndRelaunch(string packagePath)
    {
        var stagingDirectory = Path.GetDirectoryName(packagePath)!;
        var extractDirectory = Path.Combine(stagingDirectory, "extracted");
        if (Directory.Exists(extractDirectory))
        {
            Directory.Delete(extractDirectory, recursive: true);
        }

        ZipFile.ExtractToDirectory(packagePath, extractDirectory);

        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序路径。");
        var installDirectory = Path.GetDirectoryName(executablePath)!;
        var scriptPath = Path.Combine(stagingDirectory, "apply-update.ps1");
        File.WriteAllText(scriptPath, BuildUpdaterScript(packagePath, extractDirectory, installDirectory, executablePath), Encoding.UTF8);

        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList =
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                scriptPath
            }
        });
    }

    public void DiscardPackage(string packagePath)
    {
        try
        {
            var stagingDirectory = Path.GetDirectoryName(packagePath);
            if (stagingDirectory is not null && Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string GetPackageKind() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"))
            ? "self-contained"
            : "framework-dependent";

    private static string CreateStagingDirectory(string tagName)
    {
        var safeTag = string.Concat(tagName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var stagingDirectory = Path.Combine(Path.GetTempPath(), $"XProj-update-{safeTag}");
        Directory.CreateDirectory(stagingDirectory);
        return stagingDirectory;
    }

    private static string BuildUpdaterScript(string packagePath, string extractDirectory, string installDirectory, string executablePath)
    {
        var processId = Environment.ProcessId;
        var builder = new StringBuilder();
        builder.AppendLine("$ErrorActionPreference = 'Stop'");
        builder.AppendLine($"$package = '{EscapePowerShell(packagePath)}'");
        builder.AppendLine($"$source = '{EscapePowerShell(extractDirectory)}'");
        builder.AppendLine($"$target = '{EscapePowerShell(installDirectory)}'");
        builder.AppendLine($"$exe = '{EscapePowerShell(executablePath)}'");
        builder.AppendLine($"while (Get-Process -Id {processId} -ErrorAction SilentlyContinue) {{ Start-Sleep -Milliseconds 500 }}");
        builder.AppendLine("Start-Sleep -Milliseconds 500");
        // /MIR 严格镜像目标目录（清理新版已删除的文件）；robocopy 会跳过大小和修改时间均相同的文件，
        // 因此自包含包中未变化的 .NET 运行时文件不会被无意义地覆盖。
        builder.AppendLine("robocopy $source $target /MIR /COPY:DAT /DCOPY:T /R:10 /W:1 /NFL /NDL /NP /NJH | Out-Null");
        builder.AppendLine("if ($LASTEXITCODE -ge 8) { throw \"robocopy 镜像失败，退出代码 $LASTEXITCODE\" }");
        builder.AppendLine("Start-Process -FilePath $exe -WorkingDirectory $target");
        builder.AppendLine("Remove-Item -Path $package -Force -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item -Path $source -Recurse -Force -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue");
        return builder.ToString();
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''");

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"XProj/{UpdateChecker.CurrentVersionDisplay}");
        return httpClient;
    }
}
