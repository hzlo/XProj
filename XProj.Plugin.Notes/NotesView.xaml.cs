using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Markdig;
using XProj.Plugin.Abstractions;

namespace XProj.Plugin.Notes;

public partial class NotesView : UserControl
{
    private readonly NotesStore _store;
    private readonly PluginHostContext _context;
    private readonly DispatcherTimer _saveTimer;
    private readonly MarkdownPipeline _markdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();
    private NoteDocument? _selectedDocument;
    private bool _isLoading;
    private bool _hasPendingSave;

    public NotesView(PluginHostContext context)
    {
        InitializeComponent();
        _context = context;
        _store = new NotesStore(context.DataDirectory);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _saveTimer.Tick += SaveTimer_Tick;
        Loaded += NotesView_Loaded;
        Unloaded += NotesView_Unloaded;
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

    private void RefreshDocuments(string? selectPath = null)
    {
        var search = SearchTextBox.Text.Trim();
        var documents = _store.ListDocuments()
            .Where(document => string.IsNullOrWhiteSpace(search) || document.FileName.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();

        NotesListBox.ItemsSource = documents;
        var selected = documents.FirstOrDefault(document => document.FullPath == selectPath)
            ?? documents.FirstOrDefault(document => document.FullPath == _selectedDocument?.FullPath)
            ?? documents.FirstOrDefault();
        NotesListBox.SelectedItem = selected;
        if (selected is null)
        {
            ClearEditor();
        }
    }

    private async void NotesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || NotesListBox.SelectedItem is not NoteDocument document)
        {
            return;
        }

        await SavePendingAsync();
        _selectedDocument = document;
        _isLoading = true;
        try
        {
            EditorTitleTextBlock.Text = document.FileName;
            EditorTextBox.Text = await _store.ReadAsync(document);
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
        RenderPreview(EditorTextBox.Text);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshDocuments();

    private void NewNote_Click(object sender, RoutedEventArgs e)
    {
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

        await SavePendingAsync();
        var deletedName = _selectedDocument.FileName;
        _store.Delete(_selectedDocument);
        _selectedDocument = null;
        RefreshDocuments();
        _context.SetStatus?.Invoke($"已删除笔记：{deletedName}");
    }

    private async void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        await SavePendingAsync();
    }

    private async Task SavePendingAsync()
    {
        if (!_hasPendingSave || _selectedDocument is null)
        {
            return;
        }

        await _store.SaveAsync(_selectedDocument, EditorTextBox.Text);
        _hasPendingSave = false;
        _context.SetStatus?.Invoke($"已保存笔记：{_selectedDocument.FileName}");
        RefreshDocuments(_selectedDocument.FullPath);
    }

    private void ClearEditor()
    {
        _selectedDocument = null;
        _isLoading = true;
        EditorTitleTextBlock.Text = "未选择笔记";
        EditorTextBox.Text = string.Empty;
        _isLoading = false;
        RenderPreview(string.Empty);
    }

    private void RenderPreview(string markdown)
    {
        var html = Markdown.ToHtml(markdown, _markdownPipeline);
        var document = "<!doctype html><html><head><meta charset=\"utf-8\"><style>" +
            "body { font-family: 'Segoe UI', 'Microsoft YaHei UI', sans-serif; color: #C0C8E4; background: #18191E; line-height: 1.65; padding: 4px 8px; }" +
            "h1, h2, h3 { color: #64A9FF; } code { font-family: Consolas, monospace; background: #23252C; padding: 2px 5px; }" +
            "pre { background: #101216; padding: 12px; overflow-x: auto; } blockquote { border-left: 3px solid #64A9FF; padding-left: 12px; color: #ACB3CE; }" +
            "a { color: #64A9FF; } table { border-collapse: collapse; } th, td { border: 1px solid #343740; padding: 6px 9px; }" +
            "</style></head><body>" + html + "</body></html>";
        PreviewBrowser.NavigateToString(document);
    }
}
