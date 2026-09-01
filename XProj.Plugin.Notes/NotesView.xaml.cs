using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Markdig;
using XProj.Plugin.Abstractions;

namespace XProj.Plugin.Notes;

public partial class NotesView : UserControl
{
    private readonly NotesStore _store;
    private readonly PluginHostContext _context;
    private readonly DispatcherTimer _saveTimer;
    private readonly DependencyPropertyDescriptor? _themeResourceDescriptor;
    private readonly MarkdownPipeline _markdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();
    private NoteDocument? _selectedDocument;
    private bool _isLoading;
    private bool _hasPendingSave;
    private int _editVersion;

    public NotesView(PluginHostContext context)
    {
        InitializeComponent();
        _context = context;
        _store = new NotesStore(context.DataDirectory);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _saveTimer.Tick += SaveTimer_Tick;
        _themeResourceDescriptor = DependencyPropertyDescriptor.FromProperty(TagProperty, typeof(NotesView));
        _themeResourceDescriptor?.AddValueChanged(this, ThemeResource_Changed);
        Loaded += NotesView_Loaded;
        Unloaded += NotesView_Unloaded;
        IsVisibleChanged += NotesView_IsVisibleChanged;
    }

    private void NotesView_Loaded(object sender, RoutedEventArgs e)
    {
        if (NotesListBox.Items.Count == 0)
        {
            RefreshDocuments();
        }
    }

    private void NotesView_Unloaded(object sender, RoutedEventArgs e)
    {
        _saveTimer.Stop();
        _ = SavePendingAsync();
    }

    private void NotesView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            RefreshPreviewTheme();
        }
    }

    private void ThemeResource_Changed(object? sender, EventArgs e) => RefreshPreviewTheme();

    private void RefreshPreviewTheme()
    {
        if (!IsLoaded)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () => RenderPreview(_selectedDocument is null ? string.Empty : EditorTextBox.Text),
            DispatcherPriority.Render);
    }

    private void RefreshDocuments(string? selectPath = null)
    {
        var search = SearchTextBox.Text.Trim();
        var allDocuments = _store.ListDocuments();
        var documents = allDocuments
            .Where(document => string.IsNullOrWhiteSpace(search) || document.FileName.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();

        NotesListBox.ItemsSource = documents;
        NotesCountTextBlock.Text = $"{documents.Length} 篇";
        EmptyListPanel.Visibility = documents.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyListTextBlock.Text = string.IsNullOrWhiteSpace(search) ? "还没有笔记" : "没有匹配的笔记";

        var selected = documents.FirstOrDefault(document => document.FullPath == selectPath)
            ?? documents.FirstOrDefault(document => document.FullPath == _selectedDocument?.FullPath)
            ?? documents.FirstOrDefault();
        NotesListBox.SelectedItem = selected;
        if (selected is null && _selectedDocument is null)
        {
            ClearEditor(allDocuments.Count > 0 && !string.IsNullOrWhiteSpace(search));
        }
    }

    private async void NotesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || NotesListBox.SelectedItem is not NoteDocument document)
        {
            return;
        }

        if (_selectedDocument?.FullPath == document.FullPath)
        {
            return;
        }

        _saveTimer.Stop();
        await SavePendingAsync(refreshDocuments: false);
        _selectedDocument = document;
        _isLoading = true;
        try
        {
            ShowDocumentWorkspace(document);
            EditorTextBox.Text = await _store.ReadAsync(document);
            UpdateEditorStatistics(EditorTextBox.Text);
            RenderPreview(EditorTextBox.Text);
            _context.SetStatus?.Invoke($"已打开笔记：{document.FileName}");
        }
        finally
        {
            _hasPendingSave = false;
            _isLoading = false;
        }
    }

    private void EditorTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || _selectedDocument is null)
        {
            return;
        }

        _hasPendingSave = true;
        _editVersion++;
        UpdateEditorStatistics(EditorTextBox.Text);
        SaveStatusTextBlock.Text = "等待保存";
        SaveStatusDot.Fill = FindBrush("WarningBrush", Brushes.DarkOrange);
        RenderPreview(EditorTextBox.Text);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshDocuments();

    private async void NewNote_Click(object sender, RoutedEventArgs e)
    {
        _saveTimer.Stop();
        await SavePendingAsync(refreshDocuments: false);
        _isLoading = true;
        try
        {
            SearchTextBox.Clear();
        }
        finally
        {
            _isLoading = false;
        }

        var document = _store.CreateDocument();
        RefreshDocuments(document.FullPath);
        _context.SetStatus?.Invoke($"已新建笔记：{document.FileName}");
    }

    private async void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDocument is null)
        {
            return;
        }

        _saveTimer.Stop();
        await SavePendingAsync(refreshDocuments: false);
        var deletedName = _selectedDocument.FileName;
        _store.Delete(_selectedDocument);
        _selectedDocument = null;
        RefreshDocuments();
        _context.SetStatus?.Invoke($"已删除笔记：{deletedName}");
    }

    private void RenameNote_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDocument is null)
        {
            return;
        }

        EditorTitleTextBlock.Visibility = Visibility.Collapsed;
        RenameTextBox.Text = _selectedDocument.DisplayName;
        RenameTextBox.Visibility = Visibility.Visible;
        RenameTextBox.Focus();
        RenameTextBox.SelectAll();
        RenameNoteButton.IsEnabled = false;
    }

    private async void RenameTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelRename();
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RenameSelectedNoteAsync();
        }
    }

    private async Task RenameSelectedNoteAsync()
    {
        if (_selectedDocument is null)
        {
            CancelRename();
            return;
        }

        _saveTimer.Stop();
        try
        {
            await SavePendingAsync(refreshDocuments: false);
            var oldName = _selectedDocument.FileName;
            _selectedDocument = _store.Rename(_selectedDocument, RenameTextBox.Text);
            ShowDocumentWorkspace(_selectedDocument);
            RefreshDocuments(_selectedDocument.FullPath);
            _context.SetStatus?.Invoke($"已重命名笔记：{oldName} -> {_selectedDocument.FileName}");
        }
        catch (Exception exception)
        {
            _context.SetStatus?.Invoke($"重命名失败：{exception.Message}");
            RenameTextBox.Focus();
            RenameTextBox.SelectAll();
        }
    }

    private void CancelRename()
    {
        RenameTextBox.Visibility = Visibility.Collapsed;
        EditorTitleTextBlock.Visibility = Visibility.Visible;
        RenameNoteButton.IsEnabled = _selectedDocument is not null;
    }

    private async void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        await SavePendingAsync();
    }

    private async Task SavePendingAsync(bool refreshDocuments = true)
    {
        if (!_hasPendingSave || _selectedDocument is null)
        {
            return;
        }

        SaveStatusTextBlock.Text = "正在保存";
        SaveStatusDot.Fill = FindBrush("AccentBrush", Brushes.DodgerBlue);
        var document = _selectedDocument;
        var content = EditorTextBox.Text;
        var editVersion = _editVersion;
        await _store.SaveAsync(document, content);

        if (!ReferenceEquals(_selectedDocument, document) || _editVersion != editVersion)
        {
            return;
        }

        _hasPendingSave = false;
        UpdateDocumentMeta(document);
        SaveStatusTextBlock.Text = "已保存";
        SaveStatusDot.Fill = FindBrush("SuccessBrush", Brushes.ForestGreen);
        _context.SetStatus?.Invoke($"已保存笔记：{document.FileName}");
        if (refreshDocuments)
        {
            _isLoading = true;
            try
            {
                RefreshDocuments(document.FullPath);
            }
            finally
            {
                _isLoading = false;
            }
        }
    }

    private void ShowDocumentWorkspace(NoteDocument document)
    {
        DocumentWorkspace.Visibility = Visibility.Visible;
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        DeleteNoteButton.IsEnabled = true;
        RenameNoteButton.IsEnabled = true;
        EditorTitleTextBlock.Text = document.DisplayName;
        CancelRename();
        UpdateDocumentMeta(document);
        SaveStatusTextBlock.Text = "已保存";
        SaveStatusDot.Fill = FindBrush("SuccessBrush", Brushes.ForestGreen);
    }

    private void UpdateDocumentMeta(NoteDocument document)
    {
        EditorMetaTextBlock.Text = $"{document.FileName}  ·  编辑于 {document.LastEditedText}";
    }

    private void UpdateEditorStatistics(string text)
    {
        EditorCharacterCountTextBlock.Text = $"{text.Length:N0} 字符";
    }

    private void ClearEditor(bool isSearchEmptyState = false)
    {
        _selectedDocument = null;
        _isLoading = true;
        EditorTitleTextBlock.Text = "未选择笔记";
        EditorTextBox.Text = string.Empty;
        _isLoading = false;
        DocumentWorkspace.Visibility = Visibility.Collapsed;
        EmptyStatePanel.Visibility = Visibility.Visible;
        DeleteNoteButton.IsEnabled = false;
        RenameNoteButton.IsEnabled = false;
        EmptyStateTitleTextBlock.Text = isSearchEmptyState ? "没有找到笔记" : "写下第一篇笔记";
        EmptyStateDescriptionTextBlock.Text = isSearchEmptyState
            ? "换个关键词试试，或者新建一篇笔记。"
            : "支持 Markdown 实时预览，内容会自动保存到本机。";
        UpdateEditorStatistics(string.Empty);
        RenderPreview(string.Empty);
    }

    private void RenderPreview(string markdown)
    {
        var html = Markdown.ToHtml(markdown, _markdownPipeline);
        var background = GetThemeColor("PanelBrush", Color.FromRgb(0x18, 0x19, 0x1E));
        var text = GetThemeColor("TextBrush", Color.FromRgb(0xC0, 0xC8, 0xE4));
        var secondary = GetThemeColor("SecondaryTextBrush", Color.FromRgb(0xAC, 0xB3, 0xCE));
        var muted = GetThemeColor("MutedTextBrush", Color.FromRgb(0x85, 0x8A, 0xA2));
        var accent = GetThemeColor("AccentBrush", Color.FromRgb(0x64, 0xA9, 0xFF));
        var panel = GetThemeColor("InsetPanelBrush", Color.FromRgb(0x23, 0x25, 0x2C));
        var border = GetThemeColor("SoftBorderBrush", Color.FromRgb(0x29, 0x2C, 0x33));
        var uiFontFamily = GetThemeFontFamily();
        var uiFontSize = GetThemeFontSize();

        if (string.IsNullOrWhiteSpace(html))
        {
            html = "<div class=\"empty\">预览会显示在这里</div>";
        }

        var document = "<!doctype html><html><head><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"><meta charset=\"utf-8\"><style>" +
            $"html, body {{ margin: 0; min-height: 100%; background: {Hex(background)}; }}" +
            $"body {{ box-sizing: border-box; max-width: 760px; margin: 0 auto; padding: 26px 32px 72px; font-family: '{CssString(uiFontFamily)}', 'Microsoft YaHei UI', 'Segoe UI', sans-serif; font-size: {uiFontSize.ToString("0.##", CultureInfo.InvariantCulture)}px; color: {Hex(text)}; line-height: 1.72; -ms-text-size-adjust: 100%; }}" +
            $"h1, h2, h3, h4 {{ color: {Hex(text)}; line-height: 1.3; margin: 1.4em 0 .65em; }}" +
            $"h1 {{ margin-top: 0; padding-bottom: .45em; border-bottom: 1px solid {Hex(border)}; font-size: 1.9em; }}" +
            "h2 { font-size: 1.45em; } h3 { font-size: 1.2em; } p { margin: .75em 0; }" +
            $"code {{ font-family: Consolas, monospace; background: {Hex(panel)}; border: 1px solid {Hex(border)}; border-radius: 5px; padding: 2px 5px; }}" +
            $"pre {{ background: {Hex(panel)}; border: 1px solid {Hex(border)}; border-radius: 10px; padding: 14px 16px; overflow-x: auto; }} pre code {{ border: 0; padding: 0; }}" +
            $"blockquote {{ margin: 1em 0; border-left: 3px solid {Hex(accent)}; padding: 8px 0 8px 14px; color: {Hex(secondary)}; }}" +
            $"a {{ color: {Hex(accent)}; }} table {{ width: 100%; border-collapse: collapse; }} th, td {{ border: 1px solid {Hex(border)}; padding: 7px 10px; text-align: left; }}" +
            "ul, ol { padding-left: 1.5em; } li { margin: .25em 0; }" +
            "li.task-list-item { list-style: none; margin-left: -1.3em; }" +
            "li.task-list-item input[type=checkbox] { margin: 0 7px 0 0; vertical-align: middle; }" +
            $"hr {{ border: none; border-top: 1px solid {Hex(border)}; }}" +
            $"img {{ max-width: 100%; }} .empty {{ color: {Hex(muted)}; padding-top: 4px; }}" +
            "</style></head><body>" + html + "</body></html>";
        PreviewBrowser.NavigateToString(document);
    }

    private static Brush FindBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private static string GetThemeFontFamily() =>
        Application.Current?.TryFindResource("UiFontFamily") is FontFamily fontFamily
            ? fontFamily.Source
            : "Microsoft YaHei UI";

    private static double GetThemeFontSize() =>
        Application.Current?.TryFindResource("UiFontSize") is double fontSize
            ? fontSize
            : 13;

    private static Color GetThemeColor(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
        {
            return brush.Color;
        }

        return fallback;
    }

    private static string Hex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string CssString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
}
