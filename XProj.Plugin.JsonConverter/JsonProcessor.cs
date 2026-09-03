using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace XProj.Plugin.JsonConverter;

public static partial class JsonProcessor
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions CompactOptions = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Format(string input, bool indented, bool sortProperties = false, bool expandEmbeddedJson = false)
    {
        var node = Parse(input);
        if (sortProperties)
        {
            node = SortNode(node);
        }

        if (expandEmbeddedJson)
        {
            node = ExpandEmbeddedJson(node);
        }

        return node.ToJsonString(indented ? IndentedOptions : CompactOptions);
    }

    public static string Escape(string input)
    {
        var escaped = JsonSerializer.Serialize(input, CompactOptions);
        return escaped.Length >= 2 ? escaped[1..^1] : escaped;
    }

    public static string Unescape(string input)
    {
        var value = input.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return JsonSerializer.Deserialize<string>(value) ?? string.Empty;
        }

        return JsonSerializer.Deserialize<string>("\"" + value + "\"") ?? string.Empty;
    }

    public static string DecodeUnicode(string input)
    {
        var decoded = UnicodeEscapeRegex().Replace(input, match =>
        {
            if (!ushort.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var codeUnit))
            {
                return match.Value;
            }

            return ((char)codeUnit).ToString();
        });

        return CombineSurrogateCodeUnits(decoded);
    }

    public static string Sort(string input, bool indented = true) => Format(input, indented, sortProperties: true);

    public static string ExpandEmbedded(string input, bool indented = true) => Format(input, indented, expandEmbeddedJson: true);

    private static JsonNode Parse(string input) =>
        JsonNode.Parse(input, nodeOptions: null, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })
        ?? throw new JsonException("JSON 内容为空。");

    private static JsonNode SortNode(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            var result = new JsonObject();
            foreach (var property in jsonObject.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                result[property.Key] = property.Value is null ? null : SortNode(property.Value);
            }

            return result;
        }

        if (node is JsonArray jsonArray)
        {
            var result = new JsonArray();
            foreach (var child in jsonArray)
            {
                result.Add(child is null ? null : SortNode(child));
            }

            return result;
        }

        return node.DeepClone();
    }

    private static JsonNode ExpandEmbeddedJson(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            var result = new JsonObject();
            foreach (var property in jsonObject)
            {
                result[property.Key] = property.Value is null ? null : ExpandEmbeddedJson(property.Value);
            }

            return result;
        }

        if (node is JsonArray jsonArray)
        {
            var result = new JsonArray();
            foreach (var child in jsonArray)
            {
                result.Add(child is null ? null : ExpandEmbeddedJson(child));
            }

            return result;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) && LooksLikeJson(text))
        {
            try
            {
                return ExpandEmbeddedJson(Parse(text));
            }
            catch (JsonException)
            {
                // A normal string can look like JSON without actually being JSON.
            }
        }

        return node.DeepClone();
    }

    private static bool LooksLikeJson(string value)
    {
        var trimmed = value.TrimStart();
        return (trimmed.StartsWith('{') && trimmed.TrimEnd().EndsWith('}')) ||
               (trimmed.StartsWith('[') && trimmed.TrimEnd().EndsWith(']'));
    }

    private static string CombineSurrogateCodeUnits(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                builder.Append(value[index++]);
                builder.Append(value[index]);
            }
            else
            {
                builder.Append(value[index]);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex("\\\\u([0-9a-fA-F]{4})")]
    private static partial Regex UnicodeEscapeRegex();
}
