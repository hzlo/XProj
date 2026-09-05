using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using XProj.Plugin.Abstractions;

namespace ProjectManager.Wpf.Infrastructure;

public sealed class PluginPackageManager
{
    private const string Repository = "hzlo/XProj";
    private static readonly Uri ReleasesEndpoint = new($"https://api.github.com/repos/{Repository}/releases?per_page=100");
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _pluginDirectory;
    private readonly HttpClient _httpClient;

    public PluginPackageManager(string pluginDirectory, HttpClient? httpClient = null)
    {
        _pluginDirectory = Path.GetFullPath(pluginDirectory);
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public void ApplyPendingUpdates()
    {
        var pendingDirectory = Path.Combine(_pluginDirectory, ".pending");
        if (!Directory.Exists(pendingDirectory))
        {
            return;
        }

        foreach (var pendingPluginDirectory in Directory.EnumerateDirectories(pendingDirectory))
        {
            var pluginId = Path.GetFileName(pendingPluginDirectory);
            var targetDirectory = Path.Combine(_pluginDirectory, pluginId);
            var backupDirectory = targetDirectory + ".previous";
            try
            {
                if (Directory.Exists(backupDirectory))
                {
                    Directory.Delete(backupDirectory, recursive: true);
                }

                if (Directory.Exists(targetDirectory))
                {
                    Directory.Move(targetDirectory, backupDirectory);
                }

                Directory.Move(pendingPluginDirectory, targetDirectory);
                if (Directory.Exists(backupDirectory))
                {
                    Directory.Delete(backupDirectory, recursive: true);
                }
            }
            catch
            {
                if (!Directory.Exists(targetDirectory) && Directory.Exists(backupDirectory))
                {
                    Directory.Move(backupDirectory, targetDirectory);
                }
            }
        }

        TryDeleteDirectory(pendingDirectory);
    }

    public async Task<PluginUpdateInfo?> GetLatestAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return null;
        }

        using var response = await _httpClient.GetAsync(ReleasesEndpoint, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"无法读取插件 Release：{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var prefix = $"plugin-{pluginId}-v";
        PluginUpdateInfo? latest = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if ((release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) ||
                (release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) ||
                !release.TryGetProperty("tag_name", out var tagProperty))
            {
                continue;
            }

            var tag = tagProperty.GetString() ?? string.Empty;
            if (!tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !Version.TryParse(tag[prefix.Length..], out var version))
            {
                continue;
            }

            var asset = release.TryGetProperty("assets", out var assets)
                ? assets.EnumerateArray().FirstOrDefault(item =>
                    item.TryGetProperty("name", out var name) &&
                    name.GetString()?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
                : default;
            if (asset.ValueKind == JsonValueKind.Undefined ||
                !asset.TryGetProperty("browser_download_url", out var assetUrlProperty))
            {
                continue;
            }

            var assetUrl = assetUrlProperty.GetString();
            if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out var downloadUri))
            {
                continue;
            }

            var candidate = new PluginUpdateInfo(
                pluginId,
                NormalizeVersion(version),
                tag,
                downloadUri);
            if (latest is null || candidate.Version > latest.Version)
            {
                latest = candidate;
            }
        }

        return latest;
    }

    public async Task<IReadOnlyList<PluginUpdateInfo>> GetLatestAvailableAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(ReleasesEndpoint, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"无法读取插件 Release：{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var latestById = new Dictionary<string, PluginUpdateInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if ((release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) ||
                (release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) ||
                !release.TryGetProperty("tag_name", out var tagProperty))
            {
                continue;
            }

            var tag = tagProperty.GetString() ?? string.Empty;
            if (!TryParsePluginTag(tag, out var pluginId, out var version))
            {
                continue;
            }

            var asset = release.TryGetProperty("assets", out var assets)
                ? assets.EnumerateArray().FirstOrDefault(item =>
                    item.TryGetProperty("name", out var name) &&
                    name.GetString()?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
                : default;
            if (asset.ValueKind == JsonValueKind.Undefined ||
                !asset.TryGetProperty("browser_download_url", out var assetUrlProperty) ||
                !Uri.TryCreate(assetUrlProperty.GetString(), UriKind.Absolute, out var downloadUri))
            {
                continue;
            }

            var displayName = release.TryGetProperty("name", out var nameProperty)
                ? nameProperty.GetString()
                : null;
            var candidate = new PluginUpdateInfo(pluginId, NormalizeVersion(version), tag, downloadUri, displayName);
            if (!latestById.TryGetValue(pluginId, out var current) || candidate.Version > current.Version)
            {
                latestById[pluginId] = candidate;
            }
        }

        return latestById.Values.OrderBy(item => item.PluginId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<PluginUpdateInfo> DownloadAndStageLatestAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestAsync(pluginId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"没有找到插件 {pluginId} 的可用 Release。");
        Directory.CreateDirectory(_pluginDirectory);

        var pendingDirectory = Path.Combine(_pluginDirectory, ".pending", pluginId);
        var downloadPath = Path.Combine(Path.GetTempPath(), $"xproj-plugin-{pluginId}-{Guid.NewGuid():N}.zip");
        var extractionDirectory = Path.Combine(Path.GetTempPath(), $"xproj-plugin-{Guid.NewGuid():N}");
        try
        {
            await using (var responseStream = await _httpClient.GetStreamAsync(latest.DownloadUri, cancellationToken).ConfigureAwait(false))
            await using (var output = File.Create(downloadPath))
            {
                await responseStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            ZipFile.ExtractToDirectory(downloadPath, extractionDirectory);
            var manifestPath = Directory.EnumerateFiles(extractionDirectory, "plugin.json", SearchOption.AllDirectories).SingleOrDefault()
                ?? throw new InvalidDataException("插件包缺少 plugin.json。");
            var packageDirectory = Path.GetDirectoryName(manifestPath)!;
            await ValidatePackageAsync(packageDirectory, pluginId, cancellationToken).ConfigureAwait(false);

            if (Directory.Exists(pendingDirectory))
            {
                Directory.Delete(pendingDirectory, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(pendingDirectory)!);
            Directory.Move(packageDirectory, pendingDirectory);
            return latest;
        }
        finally
        {
            TryDeleteFile(downloadPath);
            TryDeleteDirectory(extractionDirectory);
        }
    }

    private static async Task ValidatePackageAsync(
        string packageDirectory,
        string pluginId,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(packageDirectory, "plugin.json");
        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(stream, SerializerOptions, cancellationToken)
            ?? throw new InvalidDataException("插件清单无效。");
        if (!string.Equals(manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(manifest.EntryAssembly) ||
            !File.Exists(Path.Combine(packageDirectory, manifest.EntryAssembly)))
        {
            throw new InvalidDataException("插件清单与下载包内容不匹配。");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("XProj", "2"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));

    private static bool TryParsePluginTag(string tag, out string pluginId, out Version version)
    {
        pluginId = string.Empty;
        version = new Version(0, 0, 0);
        if (!tag.StartsWith("plugin-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var versionMarker = tag.LastIndexOf("-v", StringComparison.OrdinalIgnoreCase);
        if (versionMarker <= "plugin-".Length ||
            !Version.TryParse(tag[(versionMarker + 2)..], out var parsedVersion))
        {
            return false;
        }

        pluginId = tag["plugin-".Length..versionMarker];
        version = parsedVersion;
        return pluginId.Length > 0;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}

public sealed record PluginUpdateInfo(
    string PluginId,
    Version Version,
    string Tag,
    Uri DownloadUri,
    string? DisplayName = null);
