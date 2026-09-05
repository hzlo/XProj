using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XProj.Plugin.JsonConverter;

/// <summary>
/// 轻量 JSONPath 子集：$.a.b[0]、$['a']、[*]、.*、..、切片、多索引。
/// 不支持过滤器 [?()] 与函数，遇到会给出明确中文错误。
/// </summary>
public static class JsonPathEvaluator
{
    public static IReadOnlyList<JsonNode?> Evaluate(JsonNode? root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new JsonException("JSONPath 为空，示例：$.store.book[0].title");
        }

        path = path.Trim();
        if (path.Contains("[?(", StringComparison.Ordinal))
        {
            throw new JsonException("暂不支持过滤器 [?()]，可用树形视图手动定位节点。");
        }

        if (path.Contains('(') || path.Contains(')'))
        {
            throw new JsonException("暂不支持函数调用，仅支持属性 / 索引 / 通配 / 切片 / 递归。");
        }

        var tokens = Tokenize(path);
        var current = new List<JsonNode?> { root };
        foreach (var token in tokens)
        {
            current = Apply(current, token);
            if (current.Count == 0)
            {
                break;
            }
        }

        return current;
    }

    private enum TokenKind
    {
        Child,
        Wildcard,
        RecursiveChild,
        RecursiveWildcard,
        Index,
        MultiIndex,
        Slice,
        UnionProps,
    }

    private sealed record PathToken(TokenKind Kind, string? Name = null, List<string>? Names = null, List<int>? Indexes = null, SliceRange? Slice = null);

    private sealed record SliceRange(int? Start, int? End, int Step);

    private static List<PathToken> Tokenize(string path)
    {
        var tokens = new List<PathToken>();
        var i = 0;
        if (path[i] != '$')
        {
            throw new JsonException("JSONPath 必须以 $ 开头，示例：$.a.b[0]");
        }

        i++;
        while (i < path.Length)
        {
            var ch = path[i];
            if (ch == '.')
            {
                if (i + 1 < path.Length && path[i + 1] == '.')
                {
                    i += 2;
                    if (i < path.Length && path[i] == '*')
                    {
                        i++;
                        tokens.Add(new PathToken(TokenKind.RecursiveWildcard));
                    }
                    else if (i < path.Length && path[i] == '[')
                    {
                        // $..[0]：递归后紧跟索引，拆成两个 token。
                        tokens.Add(new PathToken(TokenKind.RecursiveWildcard));
                    }
                    else
                    {
                        var name = ReadName(path, ref i);
                        tokens.Add(new PathToken(TokenKind.RecursiveChild, name));
                    }
                }
                else
                {
                    i++;
                    if (i < path.Length && path[i] == '*')
                    {
                        i++;
                        tokens.Add(new PathToken(TokenKind.Wildcard));
                    }
                    else
                    {
                        var name = ReadName(path, ref i);
                        tokens.Add(new PathToken(TokenKind.Child, name));
                    }
                }
            }
            else if (ch == '[')
            {
                tokens.Add(ReadBracket(path, ref i));
            }
            else if (char.IsWhiteSpace(ch))
            {
                i++;
            }
            else
            {
                throw new JsonException($"JSONPath 语法错误：位置 {i} 的 '{ch}' 不合法，应使用 .属性 或 [索引]。");
            }
        }

        return tokens;
    }

    private static string ReadName(string path, ref int i)
    {
        if (i >= path.Length)
        {
            throw new JsonException("JSONPath 末尾缺少属性名。");
        }

        if (path[i] == '\'' || path[i] == '"')
        {
            return ReadQuoted(path, ref i);
        }

        var start = i;
        while (i < path.Length && (char.IsLetterOrDigit(path[i]) || path[i] is '_' or '-' or '$'))
        {
            i++;
        }

        if (start == i)
        {
            throw new JsonException($"JSONPath 语法错误：位置 {i} 缺少属性名。");
        }

        return path[start..i];
    }

    private static string ReadQuoted(string path, ref int i)
    {
        var quote = path[i];
        i++;
        var builder = new StringBuilder();
        while (i < path.Length)
        {
            var ch = path[i++];
            if (ch == '\\' && i < path.Length)
            {
                builder.Append(path[i++]);
            }
            else if (ch == quote)
            {
                return builder.ToString();
            }
            else
            {
                builder.Append(ch);
            }
        }

        throw new JsonException("JSONPath 引号未闭合。");
    }

    private static PathToken ReadBracket(string path, ref int i)
    {
        // i 指向 '['
        i++;
        SkipSpaces(path, ref i);
        if (i < path.Length && (path[i] == '\'' || path[i] == '"'))
        {
            var names = new List<string> { ReadQuoted(path, ref i) };
            SkipSpaces(path, ref i);
            while (i < path.Length && path[i] == ',')
            {
                i++;
                SkipSpaces(path, ref i);
                names.Add(ReadQuoted(path, ref i));
                SkipSpaces(path, ref i);
            }

            Expect(path, ref i, ']');
            return names.Count == 1
                ? new PathToken(TokenKind.Child, names[0])
                : new PathToken(TokenKind.UnionProps, Names: names);
        }

        if (i < path.Length && path[i] == '*')
        {
            i++;
            SkipSpaces(path, ref i);
            Expect(path, ref i, ']');
            return new PathToken(TokenKind.Wildcard);
        }

        // 切片 / 索引 / 多索引：读到 ']' 为止再解析。
        var start = i;
        while (i < path.Length && path[i] != ']')
        {
            i++;
        }

        if (i >= path.Length)
        {
            throw new JsonException("JSONPath 中括号未闭合。");
        }

        var content = path[start..i].Trim();
        i++; // 跳过 ']'
        if (content.Length == 0)
        {
            throw new JsonException("JSONPath 中括号为空。");
        }

        if (content.Contains(':'))
        {
            return new PathToken(TokenKind.Slice, Slice: ParseSlice(content));
        }

        if (content.Contains(','))
        {
            var indexes = content.Split(',').Select(part => ParseIndex(part.Trim())).ToList();
            return new PathToken(TokenKind.MultiIndex, Indexes: indexes);
        }

        return new PathToken(TokenKind.Index, Indexes: [ParseIndex(content)]);
    }

    private static void SkipSpaces(string path, ref int i)
    {
        while (i < path.Length && char.IsWhiteSpace(path[i]))
        {
            i++;
        }
    }

    private static void Expect(string path, ref int i, char expected)
    {
        SkipSpaces(path, ref i);
        if (i >= path.Length || path[i] != expected)
        {
            throw new JsonException($"JSONPath 语法错误：位置 {i} 缺少 '{expected}'。");
        }

        i++;
    }

    private static int ParseIndex(string text)
    {
        if (!int.TryParse(text, out var index))
        {
            throw new JsonException($"JSONPath 索引 '{text}' 不是整数。");
        }

        return index;
    }

    private static SliceRange ParseSlice(string content)
    {
        var parts = content.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            throw new JsonException($"JSONPath 切片 '{content}' 格式错误，示例：[0:10:2]。");
        }

        int? ParsePart(string part)
        {
            part = part.Trim();
            if (part.Length == 0)
            {
                return null;
            }

            if (!int.TryParse(part, out var value))
            {
                throw new JsonException($"JSONPath 切片 '{content}' 不是整数。");
            }

            return value;
        }

        var step = parts.Length == 3 ? ParsePart(parts[2]) ?? 1 : 1;
        if (step == 0)
        {
            throw new JsonException("JSONPath 切片步长不能为 0。");
        }

        return new SliceRange(ParsePart(parts[0]), ParsePart(parts[1]), step);
    }

    private static List<JsonNode?> Apply(List<JsonNode?> nodes, PathToken token)
    {
        var result = new List<JsonNode?>();
        foreach (var node in nodes)
        {
            switch (token.Kind)
            {
                case TokenKind.Child:
                    AddChild(node, token.Name!, result);
                    break;
                case TokenKind.UnionProps:
                    foreach (var name in token.Names!)
                    {
                        AddChild(node, name, result);
                    }

                    break;
                case TokenKind.Wildcard:
                    AddWildcard(node, result);
                    break;
                case TokenKind.RecursiveChild:
                    AddRecursive(node, token.Name!, result);
                    break;
                case TokenKind.RecursiveWildcard:
                    AddRecursiveAll(node, result);
                    break;
                case TokenKind.Index:
                    AddIndex(node, token.Indexes![0], result);
                    break;
                case TokenKind.MultiIndex:
                    foreach (var index in token.Indexes!)
                    {
                        AddIndex(node, index, result);
                    }

                    break;
                case TokenKind.Slice:
                    AddSlice(node, token.Slice!, result);
                    break;
            }
        }

        return result;
    }

    private static void AddChild(JsonNode? node, string name, List<JsonNode?> result)
    {
        if (node is JsonObject obj && obj.TryGetPropertyValue(name, out var child))
        {
            result.Add(child);
        }
    }

    private static void AddWildcard(JsonNode? node, List<JsonNode?> result)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                result.Add(property.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                result.Add(child);
            }
        }
    }

    private static void AddRecursive(JsonNode? node, string name, List<JsonNode?> result)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (property.Key == name)
                {
                    result.Add(property.Value);
                }

                AddRecursive(property.Value, name, result);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                AddRecursive(child, name, result);
            }
        }
    }

    private static void AddRecursiveAll(JsonNode? node, List<JsonNode?> result)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                result.Add(property.Value);
                AddRecursiveAll(property.Value, result);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                result.Add(child);
                AddRecursiveAll(child, result);
            }
        }
    }

    private static void AddIndex(JsonNode? node, int index, List<JsonNode?> result)
    {
        if (node is not JsonArray array || array.Count == 0)
        {
            return;
        }

        var normalized = index < 0 ? array.Count + index : index;
        if (normalized >= 0 && normalized < array.Count)
        {
            result.Add(array[normalized]);
        }
    }

    private static void AddSlice(JsonNode? node, SliceRange slice, List<JsonNode?> result)
    {
        if (node is not JsonArray array || array.Count == 0)
        {
            return;
        }

        var count = array.Count;
        int start, end;
        if (slice.Step > 0)
        {
            start = slice.Start.HasValue ? NormalizeSliceIndex(slice.Start.Value, count) : 0;
            end = slice.End.HasValue ? NormalizeSliceIndex(slice.End.Value, count) : count;
            for (var i = start; i < end; i += slice.Step)
            {
                result.Add(array[i]);
            }
        }
        else
        {
            start = slice.Start.HasValue ? NormalizeSliceIndex(slice.Start.Value, count) : count - 1;
            end = slice.End.HasValue ? NormalizeSliceIndex(slice.End.Value, count) : -1;
            for (var i = start; i > end; i += slice.Step)
            {
                result.Add(array[i]);
            }
        }
    }

    private static int NormalizeSliceIndex(int index, int count)
    {
        if (index < 0)
        {
            index += count;
        }

        return Math.Clamp(index, 0, count);
    }
}
