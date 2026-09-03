using System.IO;
using System.Security.Cryptography;

namespace XProj.Plugin.DataSync;

public sealed class SyncEngine
{
    public async Task<SyncResult> SynchronizeAsync(DataSyncSettings settings, string dataDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new InvalidOperationException("请先填写 WebDAV 地址。");
        }

        Directory.CreateDirectory(dataDirectory);
        using var client = new WebDavClient(settings);
        var manifest = await SyncManifest.LoadAsync(dataDirectory);
        var localFiles = EnumerateConfigFiles(dataDirectory).ToDictionary(path => GetRelativePath(dataDirectory, path), StringComparer.OrdinalIgnoreCase);
        var localDetails = new Dictionary<string, LocalFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in localFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            localDetails[pair.Key] = new LocalFile(pair.Key, pair.Value, await ComputeHashAsync(pair.Value, cancellationToken), File.GetLastWriteTimeUtc(pair.Value));
        }

        progress?.Report("正在读取远程文件列表...");
        var allRemoteFiles = await client.ListFilesAsync(cancellationToken);
        var result = new SyncResult();
        foreach (var file in allRemoteFiles.Where(file => !IsConfigFile(file.RelativePath) && manifest.Files.ContainsKey(file.RelativePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await client.DeleteAsync(file.RelativePath, cancellationToken);
            result.DeletedRemote++;
            manifest.Files.Remove(file.RelativePath);
            progress?.Report($"清理远端：{file.RelativePath}");
        }

        var remoteDetails = allRemoteFiles.Where(file => IsConfigFile(file.RelativePath)).ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var nextManifest = new SyncManifest();

        foreach (var path in localDetails.Keys.Union(remoteDetails.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            localDetails.TryGetValue(path, out var local);
            remoteDetails.TryGetValue(path, out var remote);
            manifest.Files.TryGetValue(path, out var previous);
            var localChanged = local is not null && (previous is null || !string.Equals(local.Hash, previous.LocalHash, StringComparison.OrdinalIgnoreCase));
            var remoteChanged = remote is not null && (previous is null || !string.Equals(remote.Fingerprint, previous.RemoteFingerprint, StringComparison.Ordinal));

            if (local is not null && remote is not null)
            {
                if (!localChanged && !remoteChanged)
                {
                    AddManifest(nextManifest, path, local, remote);
                    continue;
                }

                if (localChanged && !remoteChanged)
                {
                    await UploadAsync(client, local, progress, cancellationToken);
                    result.Uploaded++;
                }
                else if (!localChanged && remoteChanged)
                {
                    await DownloadAsync(client, remote, dataDirectory, progress, cancellationToken);
                    result.Downloaded++;
                    local = await ReadLocalFileAsync(dataDirectory, path, cancellationToken);
                }
                else
                {
                    var action = ChooseConflict(settings.ConflictStrategy, local.ModifiedUtc, remote.ModifiedUtc);
                    if (action == SyncAction.Skip)
                    {
                        result.SkippedConflicts++;
                        result.Messages.Add($"跳过冲突：{path}");
                        AddManifest(nextManifest, path, local, remote);
                        continue;
                    }

                    if (action == SyncAction.Upload)
                    {
                        await UploadAsync(client, local, progress, cancellationToken);
                        result.Uploaded++;
                    }
                    else
                    {
                        await DownloadAsync(client, remote, dataDirectory, progress, cancellationToken);
                        result.Downloaded++;
                        local = await ReadLocalFileAsync(dataDirectory, path, cancellationToken);
                    }
                }

                remote = (await client.ListFilesAsync(cancellationToken)).FirstOrDefault(file => string.Equals(file.RelativePath, path, StringComparison.OrdinalIgnoreCase)) ?? remote;
                AddManifest(nextManifest, path, local, remote);
                continue;
            }

            if (local is not null)
            {
                if (previous is not null && !localChanged && remote is null)
                {
                    await DeleteLocalAsync(local, progress, cancellationToken);
                    result.DeletedLocal++;
                }
                else
                {
                    await UploadAsync(client, local, progress, cancellationToken);
                    result.Uploaded++;
                    remote = (await client.ListFilesAsync(cancellationToken)).FirstOrDefault(file => string.Equals(file.RelativePath, path, StringComparison.OrdinalIgnoreCase));
                    if (remote is not null)
                    {
                        AddManifest(nextManifest, path, local, remote);
                    }
                }

                continue;
            }

            if (remote is not null)
            {
                await DownloadAsync(client, remote, dataDirectory, progress, cancellationToken);
                result.Downloaded++;
                local = await ReadLocalFileAsync(dataDirectory, path, cancellationToken);
                AddManifest(nextManifest, path, local, remote);
            }
        }

        await nextManifest.SaveAsync(dataDirectory);
        result.Messages.Insert(0, $"上传 {result.Uploaded}，下载 {result.Downloaded}，清理远端 {result.DeletedRemote}，清除本地 {result.DeletedLocal}，跳过冲突 {result.SkippedConflicts}");
        return result;
    }

    private static readonly HashSet<string> ExcludedConfigFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xproj-sync.json",
        "data-sync.json",
        "update-cache.json"
    };

    private static IEnumerable<string> EnumerateConfigFiles(string dataDirectory) =>
        Directory.EnumerateFiles(dataDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Where(path => IsConfigFile(GetRelativePath(dataDirectory, path)));

    private static bool IsConfigFile(string relativePath)
    {
        if (relativePath.Contains('/') || relativePath.Contains('\\') || !relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = Path.GetFileName(relativePath);
        return !ExcludedConfigFiles.Contains(name)
            && !name.StartsWith("data.backup-", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("data.invalid-", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task UploadAsync(WebDavClient client, LocalFile file, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report($"上传：{file.RelativePath}");
        await client.UploadAsync(file.FullPath, file.RelativePath, cancellationToken);
    }

    private static async Task DownloadAsync(WebDavClient client, WebDavFile file, string localDirectory, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report($"下载：{file.RelativePath}");
        await client.DownloadAsync(file, Path.Combine(localDirectory, file.RelativePath), cancellationToken);
    }

    private static Task DeleteLocalAsync(LocalFile file, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report($"清除本地：{file.RelativePath}");
        File.Delete(file.FullPath);
        return Task.CompletedTask;
    }

    private static async Task<LocalFile> ReadLocalFileAsync(string localDirectory, string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.Combine(localDirectory, path);
        return new LocalFile(path, fullPath, await ComputeHashAsync(fullPath, cancellationToken), File.GetLastWriteTimeUtc(fullPath));
    }

    private static void AddManifest(SyncManifest manifest, string path, LocalFile? local, WebDavFile? remote)
    {
        if (local is null || remote is null)
        {
            return;
        }

        manifest.Files[path] = new SyncManifestEntry
        {
            LocalHash = local.Hash,
            RemoteFingerprint = remote.Fingerprint,
            LocalModifiedUtc = local.ModifiedUtc,
            RemoteModifiedUtc = remote.ModifiedUtc
        };
    }

    private static SyncAction ChooseConflict(SyncConflictStrategy strategy, DateTimeOffset local, DateTimeOffset remote) => strategy switch
    {
        SyncConflictStrategy.LocalWins => SyncAction.Upload,
        SyncConflictStrategy.RemoteWins => SyncAction.Download,
        SyncConflictStrategy.Skip => SyncAction.Skip,
        _ => local >= remote ? SyncAction.Upload : SyncAction.Download
    };

    private static string GetRelativePath(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private sealed record LocalFile(string RelativePath, string FullPath, string Hash, DateTimeOffset ModifiedUtc);
    private enum SyncAction { Upload, Download, Skip }
}
