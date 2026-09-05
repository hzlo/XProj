using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Folding;
using Microsoft.Win32;
using XProj.Plugin.Abstractions;

namespace XProj.Plugin.JsonConverter;

public partial class JsonConverterView : UserControl
{
    private readonly PluginHostContext _context;
    private readonly JsonConverterSettings _settings;
    private readonly string _dataDirectory;
    private readonly Dictionary<TabItem, TabData> _tabs = new();
    private readonly DispatcherTimer _debounceTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly JsonFoldingStrategy _foldingStrategy = new();
    private readonly JsonColorizingTransformer _colorizer = new();
    private readonly JsonErrorRenderer _errorRenderer = new();

    private FoldingManager? _inputFolding;
    private FoldingManager? _resultFolding;
    private FoldingManager? _queryFolding;
    private CancellationTokenSource? _cts;
    private bool _switchingTabs;
    private bool _loadingSettings = true;
    private int _tabSeq;
    private long? _errorLine;
    private long? _errorColumn;

    internal void TriggerFormatForTest()
    {
        Format_Click(this, new RoutedEventArgs());
    }

    public JsonConverterView(PluginHostContext context)
    {
        InitializeComponent();
        _context = context;
        _dataDirectory = context.DataDirectory;
        _settings = JsonConverterSettings.Load(_dataDirectory);

        SetupEditors();
        ApplySettingsToControls();
        _debounceTimer.Tick += (_, _) => { _debounceTimer.Stop(); RefreshAfterEdit(); };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveAll(); };

        Loaded += (_, _) =>
        {
            if (EditorTabs.Items.Count == 0)
            {
                RestoreTabs();
            }

            RefreshFoldings();
        };
        Unloaded += (_, _) => SaveAll();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                RefreshEditorTheme();
            }
        };
        PreviewKeyDown += OnPreviewKeyDown;
        AllowDrop = true;
        Drop += OnDrop;
        DragOver += OnDragOver;
    }

    // ---------- 初始化 ----------

    private void SetupEditors()
    {
        JsonEditorTheme.Configure(InputEditor, _settings.WordWrap, isReadOnly: false);
        JsonEditorTheme.Configure(ResultEditor, _settings.WordWrap, isReadOnly: true);
        JsonEditorTheme.Configure(QueryResultEditor, _settings.WordWrap, isReadOnly: true);
        JsonEditorTheme.InstallSearch(InputEditor);
        JsonEditorTheme.InstallSearch(ResultEditor);
        JsonEditorTheme.InstallSearch(QueryResultEditor);

        InputEditor.TextArea.TextView.LineTransformers.Add(_colorizer);
        ResultEditor.TextArea.TextView.LineTransformers.Add(_colorizer);
        QueryResultEditor.TextArea.TextView.LineTransformers.Add(_colorizer);
        InputEditor.TextArea.TextView.BackgroundRenderers.Add(_errorRenderer);

        try
        {
            _inputFolding = FoldingManager.Install(InputEditor.TextArea);
            _resultFolding = FoldingManager.Install(ResultEditor.TextArea);
            _queryFolding = FoldingManager.Install(QueryResultEditor.TextArea);
        }
        catch
        {
        }

        InputEditor.TextChanged += (_, _) => OnInputChanged();
        InputEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaretStatus();
        ResultEditor.TextChanged += (_, _) => UpdateResultStats();
        try
        {
            System.Windows.DataObject.AddPastingHandler(InputEditor.TextArea, OnPaste);
        }
        catch
        {
        }
    }

    private void RefreshEditorTheme()
    {
        JsonEditorTheme.Configure(InputEditor, _settings.WordWrap, isReadOnly: false);
        JsonEditorTheme.Configure(ResultEditor, _settings.WordWrap, isReadOnly: true);
        JsonEditorTheme.Configure(QueryResultEditor, _settings.WordWrap, isReadOnly: true);
        InputEditor.TextArea.TextView.Redraw();
        ResultEditor.TextArea.TextView.Redraw();
        QueryResultEditor.TextArea.TextView.Redraw();
    }

    private void ApplySettingsToControls()
    {
        _loadingSettings = true;
        try
        {
            IndentBox.SelectedIndex = _settings.UseTabs ? 2 : _settings.IndentSize == 4 ? 1 : 0;
            SortDescendingCheck.IsChecked = _settings.SortDescending;
            CaseSensitiveCheck.IsChecked = _settings.CaseSensitiveSort;
            AllowCommentsCheck.IsChecked = _settings.AllowComments;
            AllowTrailingCommasCheck.IsChecked = _settings.AllowTrailingCommas;
            AutoFormatPasteCheck.IsChecked = _settings.AutoFormatOnPaste;
            LiveValidateCheck.IsChecked = _settings.LiveValidate;
            WordWrapCheck.IsChecked = _settings.WordWrap;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        var indentTag = (IndentBox.SelectedItem as ComboBoxItem)?.Tag as string;
        _settings.UseTabs = indentTag == "tab";
        _settings.IndentSize = indentTag == "4" ? 4 : 2;
        _settings.SortDescending = SortDescendingCheck.IsChecked == true;
        _settings.CaseSensitiveSort = CaseSensitiveCheck.IsChecked == true;
        _settings.AllowComments = AllowCommentsCheck.IsChecked == true;
        _settings.AllowTrailingCommas = AllowTrailingCommasCheck.IsChecked == true;
        _settings.AutoFormatOnPaste = AutoFormatPasteCheck.IsChecked == true;
        _settings.LiveValidate = LiveValidateCheck.IsChecked == true;
        _settings.WordWrap = WordWrapCheck.IsChecked == true;

        InputEditor.WordWrap = _settings.WordWrap;
        ResultEditor.WordWrap = _settings.WordWrap;
        QueryResultEditor.WordWrap = _settings.WordWrap;
        _settings.Save(_dataDirectory);
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    // ---------- 标签页 ----------

    private sealed class TabData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "JSON 1";
        public TabItem TabItem { get; set; } = null!;
        public TextBlock TitleBlock { get; set; } = null!;
        public string Input { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
        public string Query { get; set; } = "$";
    }

    private void RestoreTabs()
    {
        var snapshots = JsonTabStore.Load(_dataDirectory);
        _switchingTabs = true;
        try
        {
            if (snapshots.Count == 0)
            {
                CreateTab("JSON 1", string.Empty, string.Empty, "$", Guid.NewGuid().ToString("N"));
                _tabSeq = 1;
            }
            else
            {
                foreach (var snapshot in snapshots)
                {
                    CreateTab(snapshot.Title, snapshot.Input, string.Empty, snapshot.Query, snapshot.Id);
                }

                _tabSeq = snapshots.Count;
            }

            EditorTabs.SelectedIndex = 0;
        }
        finally
        {
            _switchingTabs = false;
        }

        if (EditorTabs.SelectedItem is TabItem selected && _tabs.TryGetValue(selected, out var data))
        {
            LoadTabData(data);
        }
    }

    private TabData AddTab(string? input = null, string title = "")
    {
        _tabSeq++;
        return CreateTab(string.IsNullOrWhiteSpace(title) ? $"JSON {_tabSeq}" : title, input ?? string.Empty, string.Empty, "$", Guid.NewGuid().ToString("N"));
    }

    private TabData CreateTab(string title, string input, string result, string query, string id)
    {
        var titleBlock = new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("EditorTabTitle"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 168,
        };
        var closeButton = new Button
        {
            Content = "×",
            Style = (Style)FindResource("EditorTabCloseButton"),
            ToolTip = "关闭标签",
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(titleBlock);
        header.Children.Add(closeButton);

        var tabItem = new TabItem
        {
            Header = header,
            ToolTip = title,
            Style = (Style)FindResource("EditorTabItem"),
        };
        var data = new TabData { Id = id, Title = title, TabItem = tabItem, TitleBlock = titleBlock, Input = input, Result = result, Query = query };
        closeButton.Tag = tabItem;
        closeButton.Click += CloseTab_Click;

        _tabs[tabItem] = data;
        EditorTabs.Items.Add(tabItem);
        return data;
    }

    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentTab();
        var data = AddTab();
        EditorTabs.SelectedItem = data.TabItem;
        InputEditor.Focus();
        SetStatus("已新建空标签。");
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TabItem tabItem } || !_tabs.TryGetValue(tabItem, out var data))
        {
            return;
        }

        if (_tabs.Count <= 1)
        {
            // 保留最后一个：清空即可，避免出现无标签的怪异状态。
            InputEditor.Text = string.Empty;
            ResultEditor.Text = string.Empty;
            QueryResultEditor.Text = string.Empty;
            ClearInputError();
            data.Input = string.Empty;
            data.Result = string.Empty;
            UpdateAllStats();
            return;
        }

        var removingSelected = ReferenceEquals(EditorTabs.SelectedItem, tabItem);
        if (removingSelected)
        {
            SaveCurrentTab();
        }

        _tabs.Remove(tabItem);
        EditorTabs.Items.Remove(tabItem);
        if (removingSelected && EditorTabs.Items.Count > 0)
        {
            EditorTabs.SelectedIndex = Math.Min(EditorTabs.SelectedIndex, EditorTabs.Items.Count - 1);
            if (EditorTabs.SelectedIndex < 0)
            {
                EditorTabs.SelectedIndex = 0;
            }
        }

        ScheduleSave();
        SetStatus($"已关闭标签：{data.Title}");
    }

    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingTabs || e.AddedItems.Count == 0 || e.AddedItems[0] is not TabItem current)
        {
            return;
        }

        if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is TabItem previous && _tabs.TryGetValue(previous, out var prevData))
        {
            prevData.Input = InputEditor.Text;
            prevData.Result = ResultEditor.Text;
            prevData.Query = QueryBox.Text;
        }

        if (_tabs.TryGetValue(current, out var data))
        {
            LoadTabData(data);
        }
    }

    private void LoadTabData(TabData data)
    {
        _switchingTabs = true;
        try
        {
            InputEditor.Text = data.Input;
            ResultEditor.Text = data.Result;
            QueryBox.Text = data.Query;
            QueryResultEditor.Text = string.Empty;
            QueryStatusText.Text = "输入 JSONPath 后回车，结果命中数会显示在这里";
            ClearInputError();
            UpdateAllStats();
            RefreshTree();
        }
        finally
        {
            _switchingTabs = false;
        }
    }

    private void SaveCurrentTab()
    {
        if (EditorTabs.SelectedItem is TabItem current && _tabs.TryGetValue(current, out var data))
        {
            data.Input = InputEditor.Text;
            data.Result = ResultEditor.Text;
            data.Query = QueryBox.Text;
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveAll()
    {
        SaveCurrentTab();
        var snapshots = _tabs.Values.Select(data => new JsonTabSnapshot
        {
            Id = data.Id,
            Title = data.Title,
            Input = data.Input,
            Query = data.Query,
            UpdatedAt = DateTime.UtcNow,
        }).ToList();
        JsonTabStore.Save(_dataDirectory, snapshots);
    }

    // ---------- 输入变化 / 校验 / 统计 ----------

    private void OnInputChanged()
    {
        if (_switchingTabs)
        {
            return;
        }

        if (EditorTabs.SelectedItem is TabItem current && _tabs.TryGetValue(current, out var data))
        {
            data.Input = InputEditor.Text;
        }

        UpdateInputStats();
        UpdateCaretStatus();
        _debounceTimer.Stop();
        _debounceTimer.Start();
        ScheduleSave();
    }

    private void RefreshAfterEdit()
    {
        if (_settings.LiveValidate)
        {
            LiveValidate();
        }
        else
        {
            ClearInputError();
        }

        RefreshFoldings();
        UpdateDocInfo();
    }

    private void LiveValidate()
    {
        var text = InputEditor.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ClearInputError();
            InputDocInfoText.Text = string.Empty;
            return;
        }

        // 大文档跳过实时校验，只在点击校验时检查，避免输入卡顿。
        if (text.Length > 500_000)
        {
            ClearInputError();
            return;
        }

        var result = JsonProcessor.Validate(text, _settings.ToParseOptions());
        if (result.IsValid)
        {
            ClearInputError();
        }
        else
        {
            ShowInputError(result.ErrorMessage ?? "JSON 无效", result.LineNumber, result.BytePositionInLine, announce: false);
        }

        UpdateDocInfo();
    }

    private void ShowInputError(string message, long? line, long? column, bool announce = true)
    {
        _errorLine = line;
        _errorColumn = column;
        InputErrorText.Text = line.HasValue ? $"第 {line} 行第 {column} 列：{message}" : message;
        InputErrorBar.Visibility = Visibility.Visible;

        try
        {
            if (line.HasValue && _errorRenderer is not null)
            {
                var lineNumber = (int)Math.Max(1, line.Value);
                if (lineNumber <= InputEditor.Document.LineCount)
                {
                    var docLine = InputEditor.Document.GetLineByNumber(lineNumber);
                    var col = (int)Math.Max(1, column ?? 1);
                    col = Math.Min(col, docLine.Length + 1);
                    var offset = docLine.Offset + col - 1;
                    var length = Math.Max(1, Math.Min(30, docLine.EndOffset - offset));
                    _errorRenderer.SetError(offset, length);
                    InputEditor.TextArea.TextView.Redraw();
                }
            }
        }
        catch
        {
        }

        if (announce)
        {
            SetStatus("校验失败，见输入框上方错误条。");
        }
    }

    private void ClearInputError()
    {
        _errorLine = null;
        _errorColumn = null;
        InputErrorBar.Visibility = Visibility.Collapsed;
        InputErrorText.Text = string.Empty;
        try
        {
            _errorRenderer.Clear();
            InputEditor.TextArea.TextView.Redraw();
        }
        catch
        {
        }
    }

    private void LocateError_Click(object sender, RoutedEventArgs e)
    {
        if (!_errorLine.HasValue)
        {
            return;
        }

        try
        {
            var lineNumber = (int)Math.Max(1, _errorLine.Value);
            lineNumber = Math.Min(lineNumber, InputEditor.Document.LineCount);
            var docLine = InputEditor.Document.GetLineByNumber(lineNumber);
            var col = (int)Math.Max(1, _errorColumn ?? 1);
            col = Math.Min(col, docLine.Length + 1);
            var offset = docLine.Offset + col - 1;
            var length = Math.Max(1, Math.Min(30, docLine.EndOffset - offset));
            InputEditor.Select(offset, length);
            InputEditor.TextArea.Caret.Offset = offset;
            InputEditor.TextArea.Caret.BringCaretToView();
            InputEditor.Focus();
        }
        catch
        {
        }
    }

    private void UpdateAllStats()
    {
        UpdateInputStats();
        UpdateResultStats();
        UpdateCaretStatus();
        UpdateDocInfo();
    }

    private void UpdateInputStats()
    {
        var text = InputEditor.Text ?? string.Empty;
        var lines = InputEditor.Document?.LineCount ?? text.Split('\n').Length;
        InputStatsText.Text = $"{text.Length:N0} 字符 · {lines:N0} 行";
    }

    private void UpdateResultStats()
    {
        var text = ResultEditor.Text ?? string.Empty;
        ResultStatsText.Text = text.Length == 0 ? "空" : $"{text.Length:N0} 字符 · {ResultEditor.Document?.LineCount ?? 0:N0} 行";
        if (EditorTabs.SelectedItem is TabItem current && _tabs.TryGetValue(current, out var data))
        {
            data.Result = text;
        }
    }

    private void UpdateCaretStatus()
    {
        try
        {
            var caret = InputEditor.TextArea.Caret;
            InputCaretText.Text = $"{caret.Line}:{caret.Column}";
        }
        catch
        {
        }
    }

    private void UpdateDocInfo()
    {
        try
        {
            var text = InputEditor.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 500_000)
            {
                InputDocInfoText.Text = string.Empty;
                return;
            }

            var info = JsonProcessor.GetInfo(text, _settings.ToParseOptions());
            InputDocInfoText.Text = info.NodeCount == 0
                ? string.Empty
                : $"{info.RootKind} · {info.NodeCount:N0} 节点 · 深 {info.MaxDepth}" + (info.IsJsonLines ? $" · JSONL×{info.DocumentCount}" : string.Empty);
        }
        catch
        {
        }
    }

    private void RefreshFoldings()
    {
        try
        {
            if (_inputFolding is not null && InputEditor.Document is not null)
            {
                _foldingStrategy.UpdateFoldings(_inputFolding, InputEditor.Document);
            }

            if (_resultFolding is not null && ResultEditor.Document is not null)
            {
                _foldingStrategy.UpdateFoldings(_resultFolding, ResultEditor.Document);
            }

            if (_queryFolding is not null && QueryResultEditor.Document is not null)
            {
                _foldingStrategy.UpdateFoldings(_queryFolding, QueryResultEditor.Document);
            }
        }
        catch
        {
        }
    }

    // ---------- 操作执行 ----------

    private JsonFormatOptions CurrentFormat(bool indented, bool sort = false, bool expand = false) =>
        _settings.ToFormatOptions(indented, sort, expand);

    private void SetBusy(bool busy, string? message = null)
    {
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (message is not null)
        {
            OperationStatusText.Text = message;
        }
    }

    private void SetStatus(string message)
    {
        OperationStatusText.Text = message;
        _context.SetStatus?.Invoke(message);
    }

    private async void RunJsonTransformAsync(string name, Func<string, string> transform)
    {
        var input = InputEditor.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            SetStatus("输入为空：请粘贴、拖入或打开 JSON。");
            InputEditor.Focus();
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true, $"正在{name}…");

        try
        {
            var result = await Task.Run(() => transform(input), token);
            token.ThrowIfCancellationRequested();
            ResultEditor.Text = result;
            ClearInputError();
            RefreshFoldings();
            RefreshTree();
            UpdateAllStats();
            SaveCurrentTab();
            ScheduleSave();
            RightTabs.SelectedIndex = 0;
            SetStatus($"{name}完成（{result.Length:N0} 字符）。");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"{name}已取消。");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException jsonEx)
        {
            ShowInputError(JsonProcessor.FriendlyError(jsonEx), jsonEx.LineNumber, jsonEx.BytePositionInLine);
            SetStatus($"{name}失败：输入不是有效 JSON。");
        }
        catch (Exception ex)
        {
            SetStatus($"{name}失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RunTextTransform(string name, Func<string, string> transform)
    {
        var input = InputEditor.Text ?? string.Empty;
        try
        {
            var result = transform(input);
            ResultEditor.Text = result;
            RefreshFoldings();
            UpdateResultStats();
            SaveCurrentTab();
            ScheduleSave();
            RightTabs.SelectedIndex = 0;
            SetStatus($"{name}完成（{result.Length:N0} 字符）。");
        }
        catch (Exception ex)
        {
            SetStatus($"{name}失败：{ex.Message}");
        }
    }

    private void Format_Click(object sender, RoutedEventArgs e)
    {
        var options = CurrentFormat(indented: true);
        RunJsonTransformAsync("格式化", input => JsonProcessor.Format(input, options));
    }

    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        var options = CurrentFormat(indented: false);
        RunJsonTransformAsync("压缩", input => JsonProcessor.Format(input, options));
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var options = CurrentFormat(indented: true, sort: true);
        RunJsonTransformAsync("字段排序", input => JsonProcessor.Format(input, options));
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        var options = CurrentFormat(indented: true, expand: true);
        RunJsonTransformAsync("展开内嵌 JSON", input => JsonProcessor.Format(input, options));
    }

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        var text = InputEditor.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("输入为空：请粘贴、拖入或打开 JSON。");
            return;
        }

        var result = JsonProcessor.Validate(text, _settings.ToParseOptions());
        if (result.IsValid)
        {
            ClearInputError();
            var extra = result.IsJsonLines ? $"（JSON Lines，共 {result.DocumentCount} 个文档）" : string.Empty;
            var info = JsonProcessor.GetInfo(text, _settings.ToParseOptions());
            SetStatus($"校验通过：有效 JSON{extra}，{info.NodeCount:N0} 节点，深 {info.MaxDepth}。");
        }
        else
        {
            ShowInputError(result.ErrorMessage ?? "JSON 无效", result.LineNumber, result.BytePositionInLine);
        }
    }

    private void Escape_Click(object sender, RoutedEventArgs e) =>
        RunTextTransform("转义", JsonProcessor.Escape);

    private void Unescape_Click(object sender, RoutedEventArgs e) =>
        RunTextTransform("去转义", JsonProcessor.Unescape);

    private void UnicodeDecode_Click(object sender, RoutedEventArgs e) =>
        RunTextTransform("Unicode 解码", JsonProcessor.DecodeUnicode);

    private void UnicodeEncode_Click(object sender, RoutedEventArgs e) =>
        RunTextTransform("Unicode 编码", input => JsonProcessor.EncodeUnicode(input));

    private void JsonLinesToArray_Click(object sender, RoutedEventArgs e)
    {
        var options = CurrentFormat(indented: true);
        RunJsonTransformAsync("JSONL→数组", input => JsonProcessor.JsonLinesToArray(input, options));
    }

    private void ArrayToJsonLines_Click(object sender, RoutedEventArgs e) =>
        RunJsonTransformAsync("数组→JSONL", input => JsonProcessor.ArrayToJsonLines(input, _settings.ToParseOptions()));

    private void ReplaceInput_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ResultEditor.Text))
        {
            SetStatus("没有可写回的结果。");
            return;
        }

        InputEditor.Text = ResultEditor.Text;
        ClearInputError();
        UpdateAllStats();
        RefreshTree();
        SaveCurrentTab();
        ScheduleSave();
        SetStatus("已将结果写回输入。");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            Format_Click(sender, e);
        }
    }

    // ---------- 查询 ----------

    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            RunQuery_Click(sender, e);
        }
    }

    private void RunQuery_Click(object sender, RoutedEventArgs e)
    {
        var path = QueryBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            QueryStatusText.Text = "请输入 JSONPath，示例：$.store.book[0].title";
            return;
        }

        var source = ResolveQuerySource(out var sourceName);
        if (string.IsNullOrWhiteSpace(source))
        {
            QueryStatusText.Text = "没有可查询的内容：请先在输入框粘贴 JSON。";
            return;
        }

        try
        {
            var matches = JsonProcessor.Query(source, path, _settings.ToParseOptions());
            var output = JsonProcessor.QueryToJson(source, path, indented: true, _settings.UseTabs ? 1 : _settings.IndentSize, _settings.ToParseOptions());
            QueryResultEditor.Text = output;
            RefreshFoldings();
            QueryStatusText.Text = matches.Count == 0
                ? $"在{sourceName}中 0 命中：{path}"
                : $"在{sourceName}中 {matches.Count:N0} 命中：{path}";
            SaveCurrentTab();
            ScheduleSave();
            SetStatus($"查询完成：{matches.Count:N0} 命中。");
        }
        catch (Exception ex)
        {
            QueryStatusText.Text = $"查询失败：{ex.Message}";
            SetStatus($"查询失败：{ex.Message}");
        }
    }

    private string ResolveQuerySource(out string name)
    {
        var input = InputEditor.Text ?? string.Empty;
        var result = ResultEditor.Text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(input) && JsonProcessor.Validate(input, _settings.ToParseOptions()).IsValid)
        {
            name = "输入";
            return input;
        }

        if (!string.IsNullOrWhiteSpace(result))
        {
            name = "结果";
            return result;
        }

        name = "输入";
        return input;
    }

    // ---------- 树形 ----------

    private void RefreshTree()
    {
        try
        {
            var source = ResolveTreeSource();
            if (string.IsNullOrWhiteSpace(source) || source.Length > 1_000_000)
            {
                JsonTreeView.ItemsSource = new List<JsonTreeItem>();
                return;
            }

            var items = JsonTreeBuilder.Build(source, _settings.ToParseOptions());
            JsonTreeView.ItemsSource = FilterTree(items, TreeSearchBox.Text?.Trim() ?? string.Empty);
        }
        catch
        {
            JsonTreeView.ItemsSource = new List<JsonTreeItem>();
        }
    }

    private string ResolveTreeSource()
    {
        var input = InputEditor.Text ?? string.Empty;
        var result = ResultEditor.Text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(result) && JsonProcessor.Validate(result, _settings.ToParseOptions()).IsValid)
        {
            return result;
        }

        return input;
    }

    private static List<JsonTreeItem> FilterTree(List<JsonTreeItem> items, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return items;
        }

        var filtered = new List<JsonTreeItem>();
        foreach (var item in items)
        {
            var matchedChildren = FilterTree([.. item.Children], keyword);
            var selfMatch = item.Header.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.Preview.Contains(keyword, StringComparison.OrdinalIgnoreCase);
            if (selfMatch || matchedChildren.Count > 0)
            {
                var clone = new JsonTreeItem { Header = item.Header, Preview = item.Preview, Kind = item.Kind, JsonPath = item.JsonPath };
                if (selfMatch && matchedChildren.Count == 0)
                {
                    foreach (var child in item.Children)
                    {
                        clone.Children.Add(child);
                    }
                }
                else
                {
                    foreach (var child in matchedChildren)
                    {
                        clone.Children.Add(child);
                    }
                }

                filtered.Add(clone);
            }
        }

        return filtered;
    }

    private void TreeSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshTree();

    private void ExpandTree_Click(object sender, RoutedEventArgs e) => SetTreeExpanded(true);

    private void CollapseTree_Click(object sender, RoutedEventArgs e) => SetTreeExpanded(false);

    private void SetTreeExpanded(bool expanded)
    {
        try
        {
            foreach (var item in JsonTreeView.Items)
            {
                if (JsonTreeView.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem container)
                {
                    SetExpandedRecursive(container, expanded);
                }
            }

            JsonTreeView.UpdateLayout();
        }
        catch
        {
        }
    }

    private static void SetExpandedRecursive(ItemsControl parent, bool expanded)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem container)
            {
                container.IsExpanded = expanded;
                container.UpdateLayout();
                SetExpandedRecursive(container, expanded);
            }
        }
    }

    private void CopyTreeValue_Click(object sender, RoutedEventArgs e)
    {
        if (JsonTreeView.SelectedItem is not JsonTreeItem item)
        {
            return;
        }

        TrySetClipboard(item.Preview, "已复制节点值摘要。");
    }

    private void CopyTreePath_Click(object sender, RoutedEventArgs e)
    {
        if (JsonTreeView.SelectedItem is not JsonTreeItem item)
        {
            return;
        }

        TrySetClipboard(item.JsonPath, $"已复制 JSONPath：{item.JsonPath}");
    }

    // ---------- 输入辅助 ----------

    private void PasteInput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                SetStatus("剪贴板没有文本。");
                return;
            }

            InputEditor.Paste();
            InputEditor.Focus();
            if (_settings.AutoFormatOnPaste)
            {
                Format_Click(sender, e);
            }
            else
            {
                SetStatus("已粘贴。");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"粘贴失败：{ex.Message}");
        }
    }

    private void ClearInput_Click(object sender, RoutedEventArgs e)
    {
        InputEditor.Text = string.Empty;
        ClearInputError();
        UpdateAllStats();
        SaveCurrentTab();
        ScheduleSave();
        InputEditor.Focus();
    }

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!_settings.AutoFormatOnPaste || !e.DataObject.GetDataPresent(DataFormats.UnicodeText, true))
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try
            {
                var text = InputEditor.Text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                var options = CurrentFormat(indented: true);
                ResultEditor.Text = JsonProcessor.Format(text, options);
                ClearInputError();
                RefreshFoldings();
                RefreshTree();
                UpdateAllStats();
                SetStatus("已粘贴并自动格式化（可在选项条关闭）。");
            }
            catch
            {
                SetStatus("已粘贴：内容不是有效 JSON，未自动格式化。");
            }
        });
    }

    // ---------- 文件 / 剪贴板 ----------

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开 JSON 文件",
            Filter = "JSON 文件 (*.json;*.jsonl;*.txt)|*.json;*.jsonl;*.txt|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        OpenFiles(dialog.FileNames);
    }

    private void OpenFiles(string[] files)
    {
        var opened = 0;
        foreach (var file in files.Take(10))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 10_000_000)
                {
                    var confirmed = _context.Confirm?.Invoke("文件过大", $"{info.Name} 超过 10MB，仍要打开吗？", "打开") ?? true;
                    if (!confirmed)
                    {
                        continue;
                    }
                }

                var content = File.ReadAllText(file);
                var title = Path.GetFileNameWithoutExtension(file);
                if (title.Length > 24)
                {
                    title = title[..24] + "…";
                }

                // 复用唯一的空标签，避免打开文件后堆积空标签。
                TabData data;
                if (_tabs.Count == 1
                    && string.IsNullOrWhiteSpace(InputEditor.Text)
                    && string.IsNullOrWhiteSpace(ResultEditor.Text)
                    && EditorTabs.SelectedItem is TabItem single
                    && _tabs.TryGetValue(single, out var existing))
                {
                    data = existing;
                    data.Title = title;
                    data.TitleBlock.Text = title;
                    single.ToolTip = file;
                    EditorTabs.SelectedItem = single;
                    LoadTabDataWithInput(data, content);
                }
                else
                {
                    SaveCurrentTab();
                    data = AddTab(content, title);
                    data.TabItem.ToolTip = file;
                    EditorTabs.SelectedItem = data.TabItem;
                    LoadTabDataWithInput(data, content);
                }

                opened++;
            }
            catch (Exception ex)
            {
                SetStatus($"打开失败 {Path.GetFileName(file)}：{ex.Message}");
            }
        }

        if (opened > 0)
        {
            SaveAll();
            SetStatus($"已打开 {opened} 个文件。");
        }
    }

    private void LoadTabDataWithInput(TabData data, string input)
    {
        _switchingTabs = true;
        try
        {
            data.Input = input;
            data.Result = string.Empty;
            InputEditor.Text = input;
            ResultEditor.Text = string.Empty;
            QueryResultEditor.Text = string.Empty;
            ClearInputError();
            UpdateAllStats();
            RefreshTree();
            RefreshFoldings();
        }
        finally
        {
            _switchingTabs = false;
        }
    }

    private void SaveFile_Click(object sender, RoutedEventArgs e)
    {
        var result = ResultEditor.Text ?? string.Empty;
        var input = InputEditor.Text ?? string.Empty;
        var source = !string.IsNullOrEmpty(result) ? result : input;
        if (string.IsNullOrEmpty(source))
        {
            SetStatus("没有可保存的内容。");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存 JSON 文件",
            Filter = "JSON 文件 (*.json)|*.json|JSON Lines (*.jsonl)|*.jsonl|所有文件 (*.*)|*.*",
            FileName = "output.json",
            AddExtension = true,
            DefaultExt = ".json",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, source);
            SetStatus($"已保存{(ReferenceEquals(source, result) ? "结果" : "输入")}：{Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败：{ex.Message}");
        }
    }

    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ResultEditor.Text))
        {
            SetStatus("没有可复制的结果。");
            return;
        }

        TrySetClipboard(ResultEditor.Text, "结果已复制。");
    }

    private void TrySetClipboard(string text, string successMessage)
    {
        try
        {
            Clipboard.SetText(text);
            SetStatus(successMessage);
        }
        catch (Exception ex)
        {
            SetStatus($"复制失败：{ex.Message}");
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            OpenFiles(files);
            e.Handled = true;
        }
    }
}
