using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace XProj.Plugin.JsonConverter;

/// <summary>
/// JSON 解析与宽容选项。默认允许注释与尾随逗号，对日常粘贴更友好。
/// </summary>
public sealed record JsonParseOptions
{
    public bool AllowComments { get; init; } = true;
    public bool AllowTrailingCommas { get; init; } = true;
    public int MaxDepth { get; init; } = 128;

    public static JsonParseOptions Default { get; } = new();
}

/// <summary>格式化选项。</summary>
public sealed record JsonFormatOptions
{
    public bool Indented { get; init; } = true;
    public int IndentSize { get; init; } = 2;
    public bool UseTabs { get; init; } = false;
    public bool SortProperties { get; init; } = false;
    public bool SortDescending { get; init; } = false;
    public bool CaseSensitiveSort { get; init; } = false;
    public bool ExpandEmbeddedJson { get; init; } = false;
    public JsonParseOptions ParseOptions { get; init; } = JsonParseOptions.Default;
}

/// <summary>校验结果：错误不再占用结果框，而是结构化返回行列号。</summary>
public sealed record JsonValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public long? LineNumber { get; init; }
    public long? BytePositionInLine { get; init; }
    public bool IsJsonLines { get; init; }
    public int DocumentCount { get; init; }

    public static JsonValidationResult Valid(bool isJsonLines = false, int documentCount = 1) =>
        new() { IsValid = true, IsJsonLines = isJsonLines, DocumentCount = documentCount };

    public static JsonValidationResult Invalid(string message, long? line, long? column) =>
        new() { IsValid = false, ErrorMessage = message, LineNumber = line, BytePositionInLine = column };
}

/// <summary>文档统计信息。</summary>
public sealed record JsonDocumentInfo
{
    public string RootKind { get; init; } = "未知";
    public int NodeCount { get; init; }
    public int MaxDepth { get; init; }
    public bool IsJsonLines { get; init; }
    public int DocumentCount { get; init; }
}

public static partial class JsonProcessor
{
    public const int MaxInputLength = 10_000_000;
    private const int MaxExpandDepth = 10;

    // ---- 兼容旧调用 ----

    public static string Format(string input, bool indented, bool sortProperties = false, bool expandEmbeddedJson = false) =>
        Format(input, new JsonFormatOptions
        {
            Indented = indented,
            SortProperties = sortProperties,
            ExpandEmbeddedJson = expandEmbeddedJson,
        });

    public static string Sort(string input, bool indented = true) =>
        Format(input, new JsonFormatOptions { Indented = indented, SortProperties = true });

    public static string Sort(string input, bool indented, bool descending, bool caseSensitive) =>
        Format(input, new JsonFormatOptions
        {
            Indented = indented,
            SortProperties = true,
            SortDescending = descending,
            CaseSensitiveSort = caseSensitive,
        });

    public static string ExpandEmbedded(string input, bool indented = true) =>
        Format(input, new JsonFormatOptions { Indented = indented, ExpandEmbeddedJson = true });

    // ---- 核心 API ----

    public static string Format(string input, JsonFormatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var normalized = NormalizeInput(input);
        var parseOptions = options.ParseOptions ?? JsonParseOptions.Default;

        if (TryDetectJsonLines(normalized, parseOptions, out var lines) && lines is not null)
        {
            // JSONL 必须保持每行一个文档：每行都压缩为单行，否则换行会破坏行分隔语义。
            var lineFormat = options with { Indented = false };
            var formattedLines = new string[lines.Count];
            for (var i = 0; i < lines.Count; i++)
            {
                var node = UnwrapRootJsonString(ParseNode(lines[i], parseOptions), parseOptions);
                node = ApplyTransforms(node, options);
                formattedLines[i] = Serialize(node, lineFormat);
            }

            return string.Join('\n', formattedLines);
        }

        var root = UnwrapRootJsonString(ParseNode(normalized, parseOptions), parseOptions);
        root = ApplyTransforms(root, options);
        return Serialize(root, options);
    }

    public static string Compact(string input, JsonParseOptions? parseOptions = null) =>
        Format(input, new JsonFormatOptions { Indented = false, ParseOptions = parseOptions ?? JsonParseOptions.Default });

    public static JsonValidationResult Validate(string input, JsonParseOptions? parseOptions = null, bool allowJsonLines = true)
    {
        var options = parseOptions ?? JsonParseOptions.Default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return JsonValidationResult.Invalid("内容为空：请粘贴、拖入或打开 JSON 数据。", null, null);
        }

        if (input.Length > MaxInputLength)
        {
            return JsonValidationResult.Invalid($"内容过大（{input.Length:N0} 字符），上限为 {MaxInputLength:N0} 字符，请拆分后处理。", null, null);
        }

        var normalized = StripBom(input);
        if (allowJsonLines && TryDetectJsonLines(normalized, options, out var lines) && lines is not null)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var lineResult = ValidateSingle(lines[i], options);
                if (!lineResult.IsValid)
                {
                    return lineResult;
                }
            }

            return JsonValidationResult.Valid(isJsonLines: true, documentCount: lines.Count);
        }

        var single = ValidateSingle(normalized, options);
        return single.IsValid ? JsonValidationResult.Valid() : single;
    }

    public static JsonDocumentInfo GetInfo(string input, JsonParseOptions? parseOptions = null)
    {
        var options = parseOptions ?? JsonParseOptions.Default;
        var normalized = StripBom(input ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new JsonDocumentInfo();
        }

        try
        {
            if (TryDetectJsonLines(normalized, options, out var lines) && lines is not null)
            {
                var totalNodes = 0;
                var maxDepth = 0;
                foreach (var line in lines)
                {
                    var node = ParseNode(line, options);
                    totalNodes += CountNodes(node, 0, ref maxDepth, 0);
                }

                return new JsonDocumentInfo
                {
                    RootKind = "JSON Lines",
                    NodeCount = totalNodes,
                    MaxDepth = maxDepth,
                    IsJsonLines = true,
                    DocumentCount = lines.Count,
                };
            }

            var root = ParseNode(normalized, options);
            var depth = 0;
            var count = CountNodes(root, 0, ref depth, 0);
            return new JsonDocumentInfo
            {
                RootKind = root.GetValueKind().ToString(),
                NodeCount = count,
                MaxDepth = depth,
                DocumentCount = 1,
            };
        }
        catch (JsonException)
        {
            return new JsonDocumentInfo();
        }
    }

    /// <summary>JSONPath 查询，返回命中的节点集合（DeepClone，调用方可安全序列化）。</summary>
    public static IReadOnlyList<JsonNode?> Query(string input, string path, JsonParseOptions? parseOptions = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new JsonException("JSONPath 为空，示例：$.store.book[0].title");
        }

        var options = parseOptions ?? JsonParseOptions.Default;
        var root = ParseNode(NormalizeInput(input), options);
        return JsonPathEvaluator.Evaluate(root, path.Trim());
    }

    public static string QueryToJson(string input, string path, bool indented, int indentSize, JsonParseOptions? parseOptions = null)
    {
        var matches = Query(input, path, parseOptions);
        var options = new JsonFormatOptions { Indented = indented, IndentSize = indentSize, ParseOptions = parseOptions ?? JsonParseOptions.Default };
        if (matches.Count == 1)
        {
            return Serialize(matches[0]?.DeepClone() ?? JsonValue.Create((string?)null)!, options);
        }

        var array = new JsonArray();
        foreach (var match in matches)
        {
            array.Add(match?.DeepClone());
        }

        return Serialize(array, options);
    }

    // ---- JSONL ----

    public static bool IsJsonLines(string input, JsonParseOptions? parseOptions = null)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        return TryDetectJsonLines(StripBom(input), parseOptions ?? JsonParseOptions.Default, out _);
    }

    public static string JsonLinesToArray(string input, JsonFormatOptions? options = null)
    {
        var format = options ?? new JsonFormatOptions();
        var parseOptions = format.ParseOptions;
        var normalized = NormalizeInput(input);
        if (!TryDetectJsonLines(normalized, parseOptions, out var lines) || lines is null)
        {
            // 本来就是单个文档：包一层数组，保持语义明确。
            var single = ParseNode(normalized, parseOptions);
            var wrapped = new JsonArray(single.DeepClone());
            return Serialize(wrapped, format);
        }

        var array = new JsonArray();
        foreach (var line in lines)
        {
            array.Add(ParseNode(line, parseOptions).DeepClone());
        }

        return Serialize(array, format);
    }

    public static string ArrayToJsonLines(string input, JsonParseOptions? parseOptions = null)
    {
        var options = parseOptions ?? JsonParseOptions.Default;
        var root = ParseNode(NormalizeInput(input), options);
        if (root is not JsonArray array)
        {
            // 单个对象也允许转单行 JSONL。
            return Serialize(root, new JsonFormatOptions { Indented = false, ParseOptions = options });
        }

        var builder = new StringBuilder();
        var lineOptions = new JsonFormatOptions { Indented = false, ParseOptions = options };
        for (var i = 0; i < array.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(Serialize(array[i]?.DeepClone() ?? JsonValue.Create((string?)null)!, lineOptions));
        }

        return builder.ToString();
    }

    // ---- 文本操作 ----

    public static string Escape(string input)
    {
        input ??= string.Empty;
        var escaped = JsonSerializer.Serialize(input, CompactOptions);
        return escaped.Length >= 2 ? escaped[1..^1] : escaped;
    }

    public static string Unescape(string input)
    {
        var value = (input ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        // 1) 标准带引号字符串。
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(value) ?? string.Empty;
            }
            catch (JsonException)
            {
                // 继续尝试宽容解码。
            }
        }

        // 2) 无引号的转义片段：包一层引号再解析。
        try
        {
            return JsonSerializer.Deserialize<string>("\"" + value.Replace("\"", "\\\"") + "\"") ?? string.Empty;
        }
        catch (JsonException)
        {
        }

        // 3) 兜底：手动还原常见转义，避免直接抛错让用户无从下手。
        return ManualUnescape(value);
    }

    public static string DecodeUnicode(string input)
    {
        input ??= string.Empty;
        return UnicodeEscapeRegex().Replace(input, match =>
        {
            if (!ushort.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codeUnit))
            {
                return match.Value;
            }

            return ((char)codeUnit).ToString();
        });
        // 说明：\uD83D\uDE00 这类代理对在 .NET 字符串中本就是两个 char，
        // 相邻拼接后即为正确 emoji，无需额外合并。
    }

    public static string EncodeUnicode(string input, bool encodeAsciiOnly = false)
    {
        input ??= string.Empty;
        var builder = new StringBuilder(input.Length + 16);
        foreach (var ch in input)
        {
            var code = (int)ch;
            if (ch == '"' || ch == '\\')
            {
                builder.Append('\\').Append(ch);
            }
            else if (code is < 0x20 or 0x7F)
            {
                builder.Append(@"\u").Append(code.ToString("X4"));
            }
            else if (!encodeAsciiOnly && code > 0x7F)
            {
                builder.Append(@"\u").Append(code.ToString("X4"));
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    // ---- 内部实现 ----

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static JsonSerializerOptions CreateSerializerOptions(JsonFormatOptions format)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = format.Indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        if (format.Indented)
        {
            var size = Math.Clamp(format.IndentSize, 1, 8);
            options.IndentCharacter = format.UseTabs ? '\t' : ' ';
            options.IndentSize = format.UseTabs ? 1 : size;
        }

        return options;
    }

    private static string Serialize(JsonNode node, JsonFormatOptions format) =>
        node.ToJsonString(CreateSerializerOptions(format));

    private static JsonNode ApplyTransforms(JsonNode node, JsonFormatOptions format)
    {
        var current = node;
        if (format.ExpandEmbeddedJson)
        {
            current = ExpandEmbeddedJson(current, 0);
        }

        if (format.SortProperties)
        {
            current = SortNode(current, format.SortDescending, format.CaseSensitiveSort);
        }

        return current;
    }

    private static JsonNode ParseNode(string input, JsonParseOptions options) =>
        JsonNode.Parse(input, nodeOptions: null, documentOptions: ToDocumentOptions(options))
        ?? throw new JsonException("JSON 内容为空。");

    private static JsonNode UnwrapRootJsonString(JsonNode node, JsonParseOptions options)
    {
        var current = node;
        for (var depth = 0; depth < MaxExpandDepth; depth++)
        {
            if (current is not JsonValue jsonValue
                || !jsonValue.TryGetValue<string>(out var text)
                || !LooksLikeJson(text))
            {
                break;
            }

            try
            {
                var parsed = ParseNode(StripBom(text), options);
                if (parsed is not (JsonObject or JsonArray))
                {
                    break;
                }

                current = parsed;
            }
            catch (JsonException)
            {
                // 普通字符串可能恰好以大括号或方括号开头，解析失败时保持原值。
                break;
            }
        }

        return current;
    }

    private static JsonDocumentOptions ToDocumentOptions(JsonParseOptions options) => new()
    {
        CommentHandling = options.AllowComments ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow,
        AllowTrailingCommas = options.AllowTrailingCommas,
        MaxDepth = Math.Clamp(options.MaxDepth, 1, 1024),
    };

    internal static string NormalizeInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new JsonException("JSON 内容为空：请粘贴、拖入或打开 JSON 数据。");
        }

        if (input.Length > MaxInputLength)
        {
            throw new JsonException($"内容过大（{input.Length:N0} 字符），上限为 {MaxInputLength:N0} 字符。");
        }

        return StripBom(input);
    }

    private static string StripBom(string input) =>
        input.Length > 0 && input[0] == '\uFEFF' ? input[1..] : input;

    private static JsonValidationResult ValidateSingle(string input, JsonParseOptions options)
    {
        try
        {
            _ = ParseNode(input, options);
            return JsonValidationResult.Valid();
        }
        catch (JsonException ex)
        {
            return JsonValidationResult.Invalid(FriendlyError(ex), ex.LineNumber, ex.BytePositionInLine);
        }
    }

    internal static string FriendlyError(JsonException ex)
    {
        var message = ex.Message;
        // System.Text.Json 的英文错误信息较技术化，附加行列号方便定位。
        if (ex.LineNumber.HasValue)
        {
            message += $"（第 {ex.LineNumber} 行，第 {ex.BytePositionInLine} 列附近）";
        }

        return message;
    }

    private static bool TryDetectJsonLines(string normalized, JsonParseOptions options, out List<string>? lines)
    {
        lines = null;
        var rawLines = normalized.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (rawLines.Length < 2)
        {
            return false;
        }

        // JSONL 判定：至少两行非空，且每一行都能独立解析为 JSON，
        // 且整体不能被解析为单个 JSON（否则就是普通多行格式化 JSON）。
        try
        {
            _ = ParseNode(normalized, options);
            return false;
        }
        catch (JsonException)
        {
        }

        var collected = new List<string>(rawLines.Length);
        foreach (var raw in rawLines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                _ = ParseNode(line, options);
                collected.Add(line);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        if (collected.Count < 2)
        {
            return false;
        }

        lines = collected;
        return true;
    }

    private static JsonNode SortNode(JsonNode node, bool descending, bool caseSensitive)
    {
        if (node is JsonObject jsonObject)
        {
            var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            IEnumerable<KeyValuePair<string, JsonNode?>> ordered = jsonObject.OrderBy(pair => pair.Key, comparer);
            if (descending)
            {
                ordered = ordered.Reverse();
            }

            var result = new JsonObject();
            foreach (var property in ordered)
            {
                result[property.Key] = property.Value is null ? null : SortNode(property.Value, descending, caseSensitive);
            }

            return result;
        }

        if (node is JsonArray jsonArray)
        {
            var result = new JsonArray();
            foreach (var child in jsonArray)
            {
                result.Add(child is null ? null : SortNode(child, descending, caseSensitive));
            }

            return result;
        }

        return node.DeepClone();
    }

    private static JsonNode ExpandEmbeddedJson(JsonNode node, int depth)
    {
        if (depth > MaxExpandDepth)
        {
            return node.DeepClone();
        }

        if (node is JsonObject jsonObject)
        {
            var result = new JsonObject();
            foreach (var property in jsonObject)
            {
                result[property.Key] = property.Value is null ? null : ExpandEmbeddedJson(property.Value, depth);
            }

            return result;
        }

        if (node is JsonArray jsonArray)
        {
            var result = new JsonArray();
            foreach (var child in jsonArray)
            {
                result.Add(child is null ? null : ExpandEmbeddedJson(child, depth));
            }

            return result;
        }

        if (node is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out var text)
            && LooksLikeJson(text))
        {
            try
            {
                // 只有展开结果是对象/数组才算成功，避免把普通字符串误杀。
                var parsed = JsonNode.Parse(text);
                if (parsed is JsonObject or JsonArray)
                {
                    return ExpandEmbeddedJson(parsed, depth + 1);
                }
            }
            catch (JsonException)
            {
                // 普通字符串长得像 JSON：保持原样，调用方不会感知失败。
            }
        }

        return node.DeepClone();
    }

    private static bool LooksLikeJson(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length < 2)
        {
            return false;
        }

        return (trimmed[0] == '{' && trimmed[^1] == '}')
            || (trimmed[0] == '[' && trimmed[^1] == ']');
    }

    private static string ManualUnescape(string value)
    {
        return value
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal)
            .Replace("\\b", "\b", StringComparison.Ordinal)
            .Replace("\\f", "\f", StringComparison.Ordinal)
            .Replace("\\/", "/", StringComparison.Ordinal);
    }

    private static int CountNodes(JsonNode? node, int depth, ref int maxDepth, int count)
    {
        maxDepth = Math.Max(maxDepth, depth);
        count++;
        if (depth > 256)
        {
            return count;
        }

        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (property.Value is not null)
                {
                    count = CountNodes(property.Value, depth + 1, ref maxDepth, count);
                }
                else
                {
                    count++;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null)
                {
                    count = CountNodes(child, depth + 1, ref maxDepth, count);
                }
                else
                {
                    count++;
                }
            }
        }

        return count;
    }

    [GeneratedRegex("\\\\u([0-9a-fA-F]{4})")]
    private static partial Regex UnicodeEscapeRegex();
}
