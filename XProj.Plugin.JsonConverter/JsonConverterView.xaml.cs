using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using XProj.Plugin.Abstractions;

namespace XProj.Plugin.JsonConverter;

public partial class JsonConverterView : UserControl
{
    private readonly PluginHostContext _context;
    private readonly Dictionary<TabItem, TabState> _tabStates = new();
    private int _tabNumber;
    private bool _changingTabs;

    public JsonConverterView(PluginHostContext context)
    {
        InitializeComponent();
        _context = context;
        DataObject.AddPastingHandler(InputTextBox, InputTextBox_Pasting);
        Loaded += (_, _) =>
        {
            if (EditorTabs.Items.Count == 0)
            {
                AddTab();
            }
        };
        AllowDrop = true;
        Drop += JsonConverterView_Drop;
        DragOver += JsonConverterView_DragOver;
    }

    private void InputTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText, true))
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var text = InputTextBox.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            try
            {
                InputTextBox.Text = JsonProcessor.Format(text, true);
                _context.SetStatus?.Invoke("JSON 已粘贴并自动格式化。");
            }
            catch
            {
                _context.SetStatus?.Invoke("已粘贴内容不是有效 JSON，未自动格式化。");
            }
        });
    }

    private void AddTab(string? initialText = null, string? initialResult = null)
    {
        _tabNumber++;
        var tab = new TabItem { Header = $"JSON {_tabNumber}" };
        if (EditorTabs.SelectedItem is TabItem current)
        {
            _tabStates[current] = new TabState
            {
                Input = InputTextBox.Text,
                Result = ResultTextBox.Text
            };
        }

        _tabStates[tab] = new TabState
        {
            Input = initialText ?? "{\n  \n}",
            Result = initialResult ?? string.Empty
        };
        EditorTabs.Items.Add(tab);
        EditorTabs.SelectedItem = tab;
        _changingTabs = true;
        var state = _tabStates[tab];
        InputTextBox.Text = state.Input;
        ResultTextBox.Text = state.Result;
        _changingTabs = false;
        UpdateStats();
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => AddTab();

    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingTabs || e.AddedItems.Count == 0 || e.AddedItems[0] is not TabItem tab)
        {
            return;
        }

        _changingTabs = true;
        try
        {
            if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is TabItem previous)
            {
                _tabStates[previous] = new TabState
                {
                    Input = InputTextBox.Text,
                    Result = ResultTextBox.Text
                };
            }

            var state = _tabStates.TryGetValue(tab, out var s) ? s : new TabState { Input = "{\n  \n}", Result = string.Empty };
            InputTextBox.Text = state.Input;
            ResultTextBox.Text = state.Result;
            UpdateStats();
        }
        finally
        {
            _changingTabs = false;
        }
    }

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateStats();
        if (_changingTabs || EditorTabs.SelectedItem is not TabItem tab)
        {
            return;
        }

        _tabStates[tab] = new TabState
        {
            Input = InputTextBox.Text,
            Result = _tabStates.TryGetValue(tab, out var existing) ? existing.Result : string.Empty
        };
    }

    private void ResultTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateStats();
        if (_changingTabs || EditorTabs.SelectedItem is not TabItem tab)
        {
            return;
        }

        _tabStates[tab] = new TabState
        {
            Input = _tabStates.TryGetValue(tab, out var existing) ? existing.Input : string.Empty,
            Result = ResultTextBox.Text
        };
    }

    private void UpdateStats()
    {
        InputStatsText.Text = $"{InputTextBox.Text.Length:N0} 字符";
        ResultStatsText.Text = $"{ResultTextBox.Text.Length:N0} 字符";
    }

    private void Format_Click(object sender, RoutedEventArgs e) => RunJsonOperation("格式化", () => JsonProcessor.Format(InputTextBox.Text, true));

    private void Compact_Click(object sender, RoutedEventArgs e) => RunJsonOperation("压缩", () => JsonProcessor.Format(InputTextBox.Text, false));

    private void Sort_Click(object sender, RoutedEventArgs e) => RunJsonOperation("字段排序", () => JsonProcessor.Sort(InputTextBox.Text));

    private void Expand_Click(object sender, RoutedEventArgs e) => RunJsonOperation("展开内嵌 JSON", () => JsonProcessor.ExpandEmbedded(InputTextBox.Text));

    private void Escape_Click(object sender, RoutedEventArgs e) => RunTextOperation("转义", () => JsonProcessor.Escape(InputTextBox.Text));

    private void Unescape_Click(object sender, RoutedEventArgs e) => RunTextOperation("去转义", () => JsonProcessor.Unescape(InputTextBox.Text));

    private void Unicode_Click(object sender, RoutedEventArgs e) => RunTextOperation("Unicode 解码", () => JsonProcessor.DecodeUnicode(InputTextBox.Text));

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var _ = JsonProcessor.Format(InputTextBox.Text, true);
            ResultTextBox.Text = "✓ JSON 格式有效";
            ResultStatsText.Text = $"{ResultTextBox.Text.Length:N0} 字符";
            _context.SetStatus?.Invoke("JSON 校验通过。");
        }
        catch (Exception ex)
        {
            ResultTextBox.Text = $"✗ JSON 格式无效：{ex.Message}";
            ResultStatsText.Text = $"{ResultTextBox.Text.Length:N0} 字符";
            _context.SetStatus?.Invoke("JSON 校验失败。");
        }
    }

    private void ReplaceInput_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ResultTextBox.Text))
        {
            return;
        }

        if (EditorTabs.SelectedItem is TabItem tab)
        {
            InputTextBox.Text = ResultTextBox.Text;
            _tabStates[tab] = new TabState
            {
                Input = ResultTextBox.Text,
                Result = ResultTextBox.Text
            };
            UpdateStats();
            _context.SetStatus?.Invoke("已将结果替换为输入。");
        }
    }

    private void RunJsonOperation(string name, Func<string> operation) => RunTextOperation(name, operation);

    private void RunTextOperation(string name, Func<string> operation)
    {
        try
        {
            ResultTextBox.Text = operation();
            UpdateStats();
            _context.SetStatus?.Invoke($"JSON {name}完成。");
        }
        catch (Exception exception)
        {
            ResultTextBox.Text = $"处理失败：{exception.Message}";
            UpdateStats();
            _context.SetStatus?.Invoke($"JSON {name}失败。");
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ResultTextBox.Text))
        {
            return;
        }

        Clipboard.SetText(ResultTextBox.Text);
        _context.SetStatus?.Invoke("JSON 结果已复制。");
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开 JSON 文件",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                var content = File.ReadAllText(dialog.FileName);
                if (EditorTabs.SelectedItem is TabItem tab)
                {
                    InputTextBox.Text = content;
                    _tabStates[tab] = new TabState { Input = content, Result = string.Empty };
                    UpdateStats();
                    _context.SetStatus?.Invoke($"已打开文件：{Path.GetFileName(dialog.FileName)}");
                }
            }
            catch (Exception ex)
            {
                _context.SetStatus?.Invoke($"打开文件失败：{ex.Message}");
            }
        }
    }

    private void SaveFile_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ResultTextBox.Text))
        {
            _context.SetStatus?.Invoke("没有可保存的结果。");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存 JSON 文件",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = "output.json",
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                File.WriteAllText(dialog.FileName, ResultTextBox.Text);
                _context.SetStatus?.Invoke($"已保存文件：{Path.GetFileName(dialog.FileName)}");
            }
            catch (Exception ex)
            {
                _context.SetStatus?.Invoke($"保存文件失败：{ex.Message}");
            }
        }
    }

    private void JsonConverterView_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void JsonConverterView_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length > 0)
            {
                try
                {
                    var content = File.ReadAllText(files[0]);
                    if (EditorTabs.SelectedItem is TabItem tab)
                    {
                        InputTextBox.Text = content;
                        _tabStates[tab] = new TabState { Input = content, Result = string.Empty };
                        UpdateStats();
                        _context.SetStatus?.Invoke($"已拖入文件：{Path.GetFileName(files[0])}");
                    }
                }
                catch (Exception ex)
                {
                    _context.SetStatus?.Invoke($"读取文件失败：{ex.Message}");
                }
            }
        }
    }

    private sealed class TabState
    {
        public string Input { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
    }
}
