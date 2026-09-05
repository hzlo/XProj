using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;

namespace XProj.Plugin.JsonConverter;

/// <summary>AvalonEdit 主题与行为集中配置，保证深浅色都可读。</summary>
public static class JsonEditorTheme
{
    public static void Configure(TextEditor editor, bool wordWrap, bool isReadOnly)
    {
        editor.ShowLineNumbers = true;
        editor.WordWrap = wordWrap;
        editor.IsReadOnly = isReadOnly;
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 2;
        editor.Options.ShowSpaces = false;
        editor.Options.ShowTabs = false;
        editor.Options.HighlightCurrentLine = !isReadOnly;

        editor.FontFamily = FindFontFamily("LogFontFamily", "Consolas");
        editor.FontSize = FindFontSize("LogFontSize", 12);
        editor.Background = FindBrush("InsetPanelBrush", isReadOnly ? Color.FromRgb(0x1E, 0x20, 0x26) : Color.FromRgb(0x1A, 0x1C, 0x22));
        editor.Foreground = FindBrush("TextBrush", Colors.Gainsboro);
        editor.LineNumbersForeground = FindBrush("MutedTextBrush", Colors.Gray);

        editor.TextArea.TextView.LinkTextForegroundBrush = FindBrush("AccentBrush", Colors.DodgerBlue);
        editor.TextArea.SelectionBrush = FindBrush("SelectedBrush", Color.FromRgb(0x2D, 0x4B, 0x73));
        editor.TextArea.SelectionBorder = null;
    }

    public static void InstallSearch(TextEditor editor)
    {
        try
        {
            SearchPanel.Install(editor.TextArea);
        }
        catch
        {
        }
    }

    public static FontFamily FindFontFamily(string key, string fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is FontFamily family)
            {
                return family;
            }
        }
        catch
        {
        }

        return new FontFamily(fallback);
    }

    public static double FindFontSize(string key, double fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is double size && size > 0)
            {
                return size;
            }
        }
        catch
        {
        }

        return fallback;
    }

    public static Brush FindBrush(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is Brush brush)
            {
                return brush;
            }
        }
        catch
        {
        }

        return new SolidColorBrush(fallback);
    }

    public static Brush FindBrush(string key, Brush fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is Brush brush)
            {
                return brush;
            }
        }
        catch
        {
        }

        return fallback;
    }
}

/// <summary>JSON 语法着色：键 / 字符串 / 数字 / 布尔null / 注释。</summary>
public sealed class JsonColorizingTransformer : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        var text = CurrentContext.Document.GetText(line);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var keyBrush = JsonEditorTheme.FindBrush("AccentBrush", Color.FromRgb(0x64, 0xA9, 0xFF));
        var stringBrush = JsonEditorTheme.FindBrush("LogTextBrush", Color.FromRgb(0x9E, 0xCE, 0x6A));
        var numberBrush = JsonEditorTheme.FindBrush("WarningBrush", Color.FromRgb(0xFF, 0x9F, 0x0A));
        var keywordBrush = JsonEditorTheme.FindBrush("WarningBrush", Color.FromRgb(0xC7, 0x92, 0xEA));
        var commentBrush = JsonEditorTheme.FindBrush("MutedTextBrush", Colors.Gray);

        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch) || ch is '{' or '}' or '[' or ']' or ',' or ':')
            {
                i++;
                continue;
            }

            // 注释 //
            if (ch == '/' && i + 1 < text.Length && (text[i + 1] == '/' || text[i + 1] == '*'))
            {
                ChangeLinePart(line.Offset + i, line.Offset + text.Length, e => e.TextRunProperties.SetForegroundBrush(commentBrush));
                return;
            }

            if (ch == '"')
            {
                var start = i;
                i++;
                var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (text[i] == '"')
                    {
                        i++;
                        closed = true;
                        break;
                    }

                    i++;
                }

                if (!closed)
                {
                    // 未闭合字符串：染红整段，提示错误。
                    ChangeLinePart(line.Offset + start, line.Offset + text.Length,
                        e => e.TextRunProperties.SetForegroundBrush(new SolidColorBrush(Color.FromRgb(0xFF, 0x69, 0x58))));
                    return;
                }

                // 属性名：字符串后面紧跟冒号。
                var j = i;
                while (j < text.Length && char.IsWhiteSpace(text[j]))
                {
                    j++;
                }

                var isKey = j < text.Length && text[j] == ':';
                var brush = isKey ? keyBrush : stringBrush;
                var s = start;
                var e2 = i;
                ChangeLinePart(line.Offset + s, line.Offset + e2, e => e.TextRunProperties.SetForegroundBrush(brush));
                continue;
            }

            if (ch == '-' || char.IsDigit(ch))
            {
                var start = i;
                i++;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] is '.' or 'e' or 'E' or '+' or '-'))
                {
                    i++;
                }

                ChangeLinePart(line.Offset + start, line.Offset + i,
                    e => e.TextRunProperties.SetForegroundBrush(numberBrush));
                continue;
            }

            if (char.IsLetter(ch))
            {
                var start = i;
                while (i < text.Length && char.IsLetter(text[i]))
                {
                    i++;
                }

                var word = text[start..i];
                if (word is "true" or "false" or "null")
                {
                    ChangeLinePart(line.Offset + start, line.Offset + i,
                        e => e.TextRunProperties.SetForegroundBrush(keywordBrush));
                }

                continue;
            }

            i++;
        }
    }
}

/// <summary>错误波浪线描画：只标记一行内的错误区间，不污染文本。</summary>
public sealed class JsonErrorRenderer : IBackgroundRenderer
{
    private TextSegment? _segment;

    public KnownLayer Layer => KnownLayer.Selection;

    public void SetError(int offset, int length)
    {
        _segment = length > 0 ? new TextSegment { StartOffset = offset, EndOffset = offset + length } : null;
    }

    public void Clear() => _segment = null;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_segment is null || textView.Document is null)
        {
            return;
        }

        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, _segment))
        {
            var y = rect.Bottom - 2;
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x69, 0x58)), 1.2);
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                var x = rect.Left;
                ctx.BeginFigure(new Point(x, y), false, false);
                var up = true;
                while (x < rect.Right)
                {
                    x += 4;
                    ctx.LineTo(new Point(Math.Min(x, rect.Right), up ? y - 3 : y), true, false);
                    up = !up;
                }
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }
    }
}

/// <summary>大括号折叠：按 { } [ ] 配对生成可折叠区间。</summary>
public sealed class JsonFoldingStrategy
{
    public void UpdateFoldings(FoldingManager manager, TextDocument document)
    {
        var foldings = new List<NewFolding>();
        var stack = new Stack<(char Open, int Offset)>();
        var text = document.Text;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch is '{' or '[')
            {
                stack.Push((ch, i));
            }
            else if (ch is '}' or ']')
            {
                while (stack.Count > 0)
                {
                    var (open, offset) = stack.Pop();
                    if ((open == '{' && ch == '}') || (open == '[' && ch == ']'))
                    {
                        var startLine = document.GetLineByOffset(offset);
                        var endLine = document.GetLineByOffset(i);
                        if (endLine.LineNumber > startLine.LineNumber)
                        {
                            foldings.Add(new NewFolding(offset, i + 1) { Name = open == '{' ? "{…}" : "[…]" });
                        }

                        break;
                    }
                }
            }
        }

        foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        manager.UpdateFoldings(foldings, -1);
    }
}
