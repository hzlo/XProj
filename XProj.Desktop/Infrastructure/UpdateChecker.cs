using System.Net.Http;
using System.Text.Json;

namespace ProjectManager.Wpf.Infrastructure;

internal sealed class UpdateChecker
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);
    private static readonly Uri LatestReleaseEndpoint = new("https://github.com/hzlo/XProj/releases/latest");
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly string? _cacheFilePath;
    private readonly Version _currentVersion;
    private readonly HttpClient _httpClient;

    public UpdateChecker()
        : this(SharedHttpClient, GetAssemblyVersion(), GetDefaultCacheFilePath())
    {
    }

    internal UpdateChecker(HttpClient httpClient, Version currentVersion, string? cacheFilePath = null)
    {
        _httpClient = httpClient;
        _currentVersion = NormalizeVersion(currentVersion);
        _cacheFilePath = cacheFilePath;
    }

    public static string CurrentVersionDisplay => FormatVersion(GetAssemblyVersion());

    public async Task<UpdateCheckResult> CheckAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh)
        {
            var cachedRelease = await ReadCachedReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (cachedRelease is not null && DateTimeOffset.UtcNow - cachedRelease.CheckedAtUtc < CacheDuration)
            {
                return CreateResult(cachedRelease.TagName, cachedRelease.ReleaseUrl);
            }
        }

        using var response = await _httpClient.GetAsync(
            LatestReleaseEndpoint,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("GitHub 仓库尚未创建正式 Release。");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub 返回了错误状态：{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var releaseUrl = response.RequestMessage?.RequestUri;
        var tagName = GetTagNameFromReleaseUrl(releaseUrl);
        var result = CreateResult(tagName, releaseUrl!.AbsoluteUri);
        await WriteCachedReleaseAsync(
            new CachedRelease(DateTimeOffset.UtcNow, tagName, releaseUrl.AbsoluteUri),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private UpdateCheckResult CreateResult(string tagName, string releaseUrl)
    {
        if (!TryParseReleaseVersion(tagName, out var latestVersion))
        {
            throw new InvalidOperationException($"无法识别最新版本号：{tagName}");
        }

        return new UpdateCheckResult(
            FormatVersion(_currentVersion),
            FormatVersion(latestVersion),
            tagName,
            releaseUrl,
            latestVersion > _currentVersion);
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"XProj/{CurrentVersionDisplay}");
        return httpClient;
    }

    private static string GetDefaultCacheFilePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProjectManagerWpf",
            "update-cache.json");

    private static Version GetAssemblyVersion() =>
        typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0);

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));

    private static string FormatVersion(Version version) =>
        $"{version.Major}.{Math.Max(0, version.Minor)}.{Math.Max(0, version.Build)}";

    private static bool TryParseReleaseVersion(string tagName, out Version version)
    {
        var versionText = tagName.Trim().TrimStart('v', 'V');
        var suffixIndex = versionText.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            versionText = versionText[..suffixIndex];
        }

        if (Version.TryParse(versionText, out var parsedVersion))
        {
            version = NormalizeVersion(parsedVersion);
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    private static string GetTagNameFromReleaseUrl(Uri? releaseUrl)
    {
        const string releasePathPrefix = "/hzlo/XProj/releases/tag/";
        if (releaseUrl is not null &&
            releaseUrl.Scheme == Uri.UriSchemeHttps &&
            releaseUrl.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            releaseUrl.AbsolutePath.StartsWith(releasePathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var tagName = Uri.UnescapeDataString(releaseUrl.AbsolutePath[releasePathPrefix.Length..]).Trim('/');
            if (!string.IsNullOrWhiteSpace(tagName) && !tagName.Contains('/'))
            {
                return tagName;
            }
        }

        throw new InvalidOperationException("GitHub 未返回有效的最新 Release 地址。");
    }

    private async Task<CachedRelease?> ReadCachedReleaseAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_cacheFilePath) || !File.Exists(_cacheFilePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_cacheFilePath);
            return await JsonSerializer.DeserializeAsync<CachedRelease>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task WriteCachedReleaseAsync(CachedRelease release, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_cacheFilePath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath)!);
            await using var stream = File.Create(_cacheFilePath);
            await JsonSerializer.SerializeAsync(stream, release, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record CachedRelease(DateTimeOffset CheckedAtUtc, string TagName, string ReleaseUrl);
}

internal sealed record UpdateCheckResult(
    string CurrentVersion,
    string LatestVersion,
    string LatestTag,
    string ReleaseUrl,
    bool IsUpdateAvailable);
