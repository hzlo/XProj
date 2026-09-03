using System.IO;
using System.Text.Json;

namespace XProj.Plugin.DataSync;

public enum SyncConflictStrategy
{
    LocalWins,
    RemoteWins,
    NewestWins,
    Skip
}

public sealed class DataSyncSettings
{
    public string Endpoint { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string RemoteDirectory { get; set; } = "xproj";
    public int IntervalMinutes { get; set; } = 30;
    public bool AutoSync { get; set; }
    public SyncConflictStrategy ConflictStrategy { get; set; } = SyncConflictStrategy.NewestWins;

    public static async Task<DataSyncSettings> LoadAsync(string dataDirectory)
    {
        var path = GetPath(dataDirectory);
        if (!File.Exists(path))
        {
            return new DataSyncSettings();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<DataSyncSettings>(stream) ?? new DataSyncSettings();
        }
        catch (JsonException)
        {
            return new DataSyncSettings();
        }
    }

    public async Task SaveAsync(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = GetPath(dataDirectory);
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, this, new JsonSerializerOptions { WriteIndented = true });
        }

        File.Move(temporaryPath, path, true);
    }

    private static string GetPath(string dataDirectory) => Path.Combine(dataDirectory, "data-sync.json");
}

public sealed class SyncManifest
{
    public Dictionary<string, SyncManifestEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<SyncManifest> LoadAsync(string localDirectory)
    {
        var path = Path.Combine(localDirectory, ".xproj-sync.json");
        if (!File.Exists(path))
        {
            return new SyncManifest();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var manifest = await JsonSerializer.DeserializeAsync<SyncManifest>(stream) ?? new SyncManifest();
            manifest.Files = new Dictionary<string, SyncManifestEntry>(manifest.Files, StringComparer.OrdinalIgnoreCase);
            return manifest;
        }
        catch (JsonException)
        {
            return new SyncManifest();
        }
    }

    public async Task SaveAsync(string localDirectory)
    {
        Directory.CreateDirectory(localDirectory);
        var path = Path.Combine(localDirectory, ".xproj-sync.json");
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, this, new JsonSerializerOptions { WriteIndented = true });
        }

        File.Move(temporaryPath, path, true);
    }
}

public sealed class SyncManifestEntry
{
    public string LocalHash { get; set; } = string.Empty;
    public string RemoteFingerprint { get; set; } = string.Empty;
    public DateTimeOffset LocalModifiedUtc { get; set; }
    public DateTimeOffset RemoteModifiedUtc { get; set; }
}

public sealed record WebDavFile(string RelativePath, long Length, DateTimeOffset ModifiedUtc, string Fingerprint);

public sealed class SyncResult
{
    public int Uploaded { get; set; }
    public int Downloaded { get; set; }
    public int DeletedRemote { get; set; }
    public int DeletedLocal { get; set; }
    public int SkippedConflicts { get; set; }
    public List<string> Messages { get; } = new();
}
