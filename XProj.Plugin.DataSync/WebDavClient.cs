using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;

namespace XProj.Plugin.DataSync;

public sealed class WebDavClient : IDisposable
{
    private static readonly XNamespace Dav = "DAV:";
    private readonly HttpClient _client;

    public WebDavClient(DataSyncSettings settings)
    {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            handler.Credentials = new NetworkCredential(settings.Username, settings.Password);
            handler.PreAuthenticate = true;
        }

        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        Endpoint = NormalizeEndpoint(settings.Endpoint);
        RemoteDirectory = NormalizeRelativePath(settings.RemoteDirectory);
    }

    public Uri Endpoint { get; }
    public string RemoteDirectory { get; }

    public async Task<IReadOnlyList<WebDavFile>> ListFilesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCollectionAsync(RemoteDirectory, cancellationToken);
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), GetRemoteUri(RemoteDirectory));
        request.Headers.TryAddWithoutValidation("Depth", "infinity");
        request.Content = new StringContent("<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:getcontentlength/><d:getlastmodified/><d:getetag/><d:resourcetype/></d:prop></d:propfind>", Encoding.UTF8, "application/xml");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        return ParseFiles(body);
    }

    public async Task DownloadAsync(WebDavFile file, string destination, CancellationToken cancellationToken = default)
    {
        var temporaryPath = destination + ".download";
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
        using var response = await _client.GetAsync(GetRemoteUri(ToRemotePath(file.RelativePath)), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = File.Create(temporaryPath))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        File.Move(temporaryPath, destination, true);
    }

    public async Task UploadAsync(string source, string relativePath, CancellationToken cancellationToken = default)
    {
        var remotePath = ToRemotePath(relativePath);
        await EnsureCollectionAsync(Path.GetDirectoryName(remotePath)?.Replace('\\', '/') ?? string.Empty, cancellationToken);
        await using var stream = File.OpenRead(source);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await _client.PutAsync(GetRemoteUri(remotePath), content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        using var response = await _client.DeleteAsync(GetRemoteUri(ToRemotePath(relativePath)), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task EnsureCollectionAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var segments = NormalizeRelativePath(relativePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;
        foreach (var segment in segments)
        {
            current = string.IsNullOrEmpty(current) ? segment : current + "/" + segment;
            using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), GetRemoteUri(current));
            using var response = await _client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MethodNotAllowed || response.StatusCode == HttpStatusCode.Conflict)
            {
                continue;
            }

            response.EnsureSuccessStatusCode();
        }
    }

    private IReadOnlyList<WebDavFile> ParseFiles(string xml)
    {
        var document = XDocument.Parse(xml);
        var files = new List<WebDavFile>();
        foreach (var response in document.Descendants(Dav + "response"))
        {
            var href = response.Element(Dav + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var prop = response.Descendants(Dav + "prop").FirstOrDefault();
            if (prop?.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null)
            {
                continue;
            }

            var relativePath = GetRelativePath(href);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            _ = long.TryParse(prop?.Element(Dav + "getcontentlength")?.Value, out var length);
            _ = DateTimeOffset.TryParse(prop?.Element(Dav + "getlastmodified")?.Value, out var modified);
            var fingerprint = prop?.Element(Dav + "getetag")?.Value.Trim('"') ?? $"{length}:{modified:O}";
            files.Add(new WebDavFile(relativePath, length, modified, fingerprint));
        }

        return files;
    }

    private string GetRelativePath(string href)
    {
        var uri = Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute : new Uri(Endpoint, href);
        var endpointPath = Endpoint.AbsolutePath.TrimEnd('/') + "/" + RemoteDirectory;
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var index = path.IndexOf(endpointPath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return string.Empty;
        }

        return NormalizeRelativePath(path[(index + endpointPath.TrimEnd('/').Length)..]);
    }

    private string ToRemotePath(string relativePath) => string.IsNullOrWhiteSpace(RemoteDirectory) ? NormalizeRelativePath(relativePath) : RemoteDirectory + "/" + NormalizeRelativePath(relativePath);

    private Uri GetRemoteUri(string path)
    {
        var builder = new UriBuilder(Endpoint);
        var endpointPath = builder.Path.TrimEnd('/');
        var encodedPath = string.Join('/', NormalizeRelativePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        builder.Path = endpointPath + (encodedPath.Length == 0 ? string.Empty : "/" + encodedPath);
        return builder.Uri;
    }

    private static Uri NormalizeEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint.Trim() + (endpoint.Trim().EndsWith('/') ? string.Empty : "/"), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("WebDAV 地址必须是 http 或 https URL。");
        }

        return uri;
    }

    private static string NormalizeRelativePath(string path) => string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(segment => segment is not "." and not ".."));

    public void Dispose() => _client.Dispose();
}
