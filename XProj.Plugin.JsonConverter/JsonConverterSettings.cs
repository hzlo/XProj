using System.IO;
using System.Text.Json;

namespace XProj.Plugin.JsonConverter;

/// <summary>JSON 工具设置：持久化到插件数据目录，解决原来“关闭即丢、无法配置”问题。</summary>
public sealed class JsonConverterSettings
{
    public int IndentSize { get; set; } = 2;
    public bool UseTabs { get; set; }
    public bool SortDescending { get; set; }
    public bool CaseSensitiveSort { get; set; }
    public bool AllowComments { get; set; } = true;
    public bool AllowTrailingCommas { get; set; } = true;
    public bool AutoFormatOnPaste { get; set; }
    public bool WordWrap { get; set; }
    public bool LiveValidate { get; set; } = true;
    public bool SyncTreeWithResult { get; set; } = true;

    public JsonParseOptions ToParseOptions() => new()
    {
        AllowComments = AllowComments,
        AllowTrailingCommas = AllowTrailingCommas,
    };

    public JsonFormatOptions ToFormatOptions(bool indented, bool sort = false, bool expand = false) => new()
    {
        Indented = indented,
        IndentSize = IndentSize,
        UseTabs = UseTabs,
        SortProperties = sort,
        SortDescending = SortDescending,
        CaseSensitiveSort = CaseSensitiveSort,
        ExpandEmbeddedJson = expand,
        ParseOptions = ToParseOptions(),
    };

    public static JsonConverterSettings Load(string dataDirectory)
    {
        try
        {
            var path = GetPath(dataDirectory);
            if (!File.Exists(path))
            {
                return new JsonConverterSettings();
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<JsonConverterSettings>(json);
            return Normalize(settings ?? new JsonConverterSettings());
        }
        catch
        {
            return new JsonConverterSettings();
        }
    }

    public void Save(string dataDirectory)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GetPath(dataDirectory))!);
            var normalized = Normalize(this);
            var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true });
            var path = GetPath(dataDirectory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, true);
        }
        catch
        {
            // 设置保存失败不应打断主流程。
        }
    }

    private static JsonConverterSettings Normalize(JsonConverterSettings settings)
    {
        settings.IndentSize = Math.Clamp(settings.IndentSize, 1, 8);
        return settings;
    }

    private static string GetPath(string dataDirectory) =>
        Path.Combine(dataDirectory, "json-converter", "settings.json");
}
