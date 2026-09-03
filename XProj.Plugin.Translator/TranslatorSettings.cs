using System.IO;
using System.Text.Json;

namespace XProj.Plugin.Translator;

public sealed class TranslatorSettings
{
    public string Provider { get; set; } = "Google";
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "zh-CN";
    public string TencentSecretId { get; set; } = string.Empty;
    public string TencentSecretKey { get; set; } = string.Empty;
    public string TencentRegion { get; set; } = "ap-beijing";
    public string AliAccessKeyId { get; set; } = string.Empty;
    public string AliAccessKeySecret { get; set; } = string.Empty;
    public string AliEndpoint { get; set; } = "mt.cn-hangzhou.aliyuncs.com";
    public string GoogleApiKey { get; set; } = string.Empty;
    public bool GoogleUsePublicEndpoint { get; set; }

    public static async Task<TranslatorSettings> LoadAsync(string dataDirectory)
    {
        var path = GetPath(dataDirectory);
        if (!File.Exists(path))
        {
            return new TranslatorSettings();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<TranslatorSettings>(stream) ?? new TranslatorSettings();
        }
        catch (JsonException)
        {
            return new TranslatorSettings();
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

    private static string GetPath(string dataDirectory) => Path.Combine(dataDirectory, "translator.json");
}
