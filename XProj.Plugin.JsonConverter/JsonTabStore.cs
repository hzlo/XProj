using System.IO;
using System.Text.Json;

namespace XProj.Plugin.JsonConverter;

/// <summary>标签页快照：用于重启/切换后恢复，解决原来内存字典丢数据问题。</summary>
public sealed class JsonTabSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "JSON 1";
    public string Input { get; set; } = string.Empty;
    public string Query { get; set; } = "$";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class JsonTabStore
{
    private const int MaxTabs = 20;
    private const int MaxCharsPerTab = 1_000_000;

    public static List<JsonTabSnapshot> Load(string dataDirectory)
    {
        try
        {
            var path = GetPath(dataDirectory);
            if (!File.Exists(path))
            {
                return [];
            }

            var json = File.ReadAllText(path);
            var tabs = JsonSerializer.Deserialize<List<JsonTabSnapshot>>(json);
            if (tabs is null || tabs.Count == 0)
            {
                return [];
            }

            return tabs
                .Where(tab => tab is not null)
                .Take(MaxTabs)
                .Select(tab =>
                {
                    tab.Id = string.IsNullOrWhiteSpace(tab.Id) ? Guid.NewGuid().ToString("N") : tab.Id;
                    tab.Title = string.IsNullOrWhiteSpace(tab.Title) ? "JSON" : tab.Title;
                    tab.Input ??= string.Empty;
                    tab.Query = string.IsNullOrWhiteSpace(tab.Query) ? "$" : tab.Query;
                    return tab;
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static void Save(string dataDirectory, IReadOnlyList<JsonTabSnapshot> tabs)
    {
        try
        {
            var path = GetPath(dataDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var truncated = tabs
                .Take(MaxTabs)
                .Select(tab => new JsonTabSnapshot
                {
                    Id = tab.Id,
                    Title = tab.Title,
                    Input = Truncate(tab.Input),
                    Query = Truncate(tab.Query, 10_000),
                    UpdatedAt = tab.UpdatedAt,
                })
                .ToList();
            var json = JsonSerializer.Serialize(truncated, new JsonSerializerOptions { WriteIndented = false });
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, true);
        }
        catch
        {
        }
    }

    private static string Truncate(string? value, int max = MaxCharsPerTab)
    {
        value ??= string.Empty;
        return value.Length <= max ? value : value[..max];
    }

    private static string GetPath(string dataDirectory) =>
        Path.Combine(dataDirectory, "json-converter", "tabs.json");
}
