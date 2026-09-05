using System.Text.Json;
using System.Text.Json.Nodes;

namespace XProj.Plugin.JsonConverter;

/// <summary>树形视图模型：Header 显示属性名，Preview 显示值摘要，JsonPath 支持复制与定位。</summary>
public sealed class JsonTreeItem
{
    public string Header { get; init; } = string.Empty;
    public string Preview { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string JsonPath { get; init; } = "$";
    public List<JsonTreeItem> Children { get; } = [];
}

public static class JsonTreeBuilder
{
    private const int MaxChildren = 2000;
    private const int MaxPreviewLength = 120;

    public static List<JsonTreeItem> Build(string input, JsonParseOptions? parseOptions = null)
    {
        try
        {
            var normalized = input.Trim();
            if (normalized.Length == 0)
            {
                return [];
            }

            JsonNode root;
            try
            {
                root = JsonNode.Parse(normalized) ?? JsonValue.Create((string?)null)!;
            }
            catch (JsonException)
            {
                // 尝试 JSONL：每行一个文档。
                var lines = normalized.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2)
                {
                    return [];
                }

                var array = new JsonArray();
                foreach (var line in lines)
                {
                    array.Add(JsonNode.Parse(line.Trim()));
                }

                root = array;
            }

            return BuildFromNode(root, "$", "root");
        }
        catch
        {
            return [];
        }
    }

    public static List<JsonTreeItem> BuildFromNode(JsonNode? node, string path, string name)
    {
        var item = CreateItem(node, path, name);
        if (node is JsonObject obj)
        {
            var count = 0;
            foreach (var property in obj)
            {
                if (count++ >= MaxChildren)
                {
                    item.Children.Add(new JsonTreeItem
                    {
                        Header = $"… 还有 {obj.Count - MaxChildren} 个属性未展示",
                        Kind = "More",
                        JsonPath = path,
                    });
                    break;
                }

                var childPath = $"{path}.{EscapePathSegment(property.Key)}";
                item.Children.AddRange(BuildFromNode(property.Value, childPath, property.Key));
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count && i < MaxChildren; i++)
            {
                item.Children.AddRange(BuildFromNode(array[i], $"{path}[{i}]", $"[{i}]"));
            }

            if (array.Count > MaxChildren)
            {
                item.Children.Add(new JsonTreeItem
                {
                    Header = $"… 还有 {array.Count - MaxChildren} 项未展示",
                    Kind = "More",
                    JsonPath = path,
                });
            }
        }

        return [item];
    }

    private static JsonTreeItem CreateItem(JsonNode? node, string path, string name)
    {
        if (node is JsonObject obj)
        {
            return new JsonTreeItem { Header = name, Preview = $"{{ {obj.Count} 个属性 }}", Kind = "Object", JsonPath = path };
        }

        if (node is JsonArray array)
        {
            return new JsonTreeItem { Header = name, Preview = $"[ {array.Count} 项 ]", Kind = "Array", JsonPath = path };
        }

        if (node is JsonValue value)
        {
            var kind = value.GetValueKind();
            var preview = kind switch
            {
                JsonValueKind.String => $"\"{Truncate(value.GetValue<string>())}\"",
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null or JsonValueKind.Undefined => "null",
                _ => value.ToString(),
            };
            return new JsonTreeItem { Header = name, Preview = preview, Kind = kind.ToString(), JsonPath = path };
        }

        return new JsonTreeItem { Header = name, Preview = "null", Kind = "Null", JsonPath = path };
    }

    private static string Truncate(string value)
    {
        value = value.Replace('\n', ' ').Replace('\r', ' ');
        return value.Length <= MaxPreviewLength ? value : value[..MaxPreviewLength] + "…";
    }

    private static string EscapePathSegment(string segment)
    {
        if (segment.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '$'))
        {
            return segment;
        }

        return $"['{segment.Replace("'", "\\'", StringComparison.Ordinal)}']";
    }
}
