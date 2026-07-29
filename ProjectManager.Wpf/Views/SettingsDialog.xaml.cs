using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ProjectManager.Wpf.Models;

namespace ProjectManager.Wpf.Views;

public partial class SettingsDialog : Window
{
    private readonly Func<string, Task> _exportConfiguration;
    private readonly Func<string, Task<AppSettings>> _importConfiguration;
    private readonly Func<Window, Task> _checkForUpdates;
    private readonly List<FontFamily> _fonts;
    private AppSettings _settings;

    public SettingsDialog(
        AppSettings settings,
        Func<string, Task> exportConfiguration,
        Func<string, Task<AppSettings>> importConfiguration,
        string currentVersion,
        Func<Window, Task> checkForUpdates)
    {
        InitializeComponent();
        _settings = settings.Clone();
        _exportConfiguration = exportConfiguration;
        _importConfiguration = importConfiguration;
        _checkForUpdates = checkForUpdates;
        _fonts = Fonts.SystemFontFamilies.OrderBy(font => font.Source, StringComparer.CurrentCultureIgnoreCase).ToList();

        ThemeComboBox.ItemsSource = new[] { "深色", "浅色" };
        CloseBehaviorComboBox.ItemsSource = new[] { "最小化到系统托盘", "完全退出" };
        UiFontComboBox.ItemsSource = _fonts;
        LogFontComboBox.ItemsSource = _fonts;
        UiFontSizeComboBox.ItemsSource = Enumerable.Range(10, 15).Select(size => (double)size);
        LogFontSizeComboBox.ItemsSource = Enumerable.Range(8, 33).Select(size => (double)size);
        LogVisibleLineCountComboBox.ItemsSource = new[] { 100, 300, 500, 1000 };
        CurrentVersionText.Text = $"当前版本 v{currentVersion}";
        ApplySettingsToControls(_settings);
    }

    public AppSettings? Result { get; private set; }

    private void ApplySettingsToControls(AppSettings settings)
    {
        ThemeComboBox.SelectedIndex = settings.Theme == "Light" ? 1 : 0;
        CloseBehaviorComboBox.SelectedIndex = settings.CloseBehavior == "Exit" ? 1 : 0;
        UiFontComboBox.SelectedItem = FindFont(settings.UiFontFamily);
        UiFontSizeComboBox.SelectedItem = Math.Round(settings.UiFontSize);
        LogFontComboBox.SelectedItem = FindFont(settings.LogFontFamily);
        LogFontSizeComboBox.SelectedItem = Math.Round(settings.LogFontSize);
        LogBoldToggle.IsChecked = settings.LogFontBold;
        LogItalicToggle.IsChecked = settings.LogFontItalic;
        LogVisibleLineCountComboBox.SelectedItem = settings.LogVisibleLineCount;
        UpdatePreviews();
    }

    private FontFamily FindFont(string source) =>
        _fonts.FirstOrDefault(font => font.Source.Equals(source, StringComparison.OrdinalIgnoreCase)) ?? _fonts.First();

    private void FontSelectionChanged(object sender, RoutedEventArgs e) => UpdatePreviews();

    private void UpdatePreviews()
    {
        if (UiFontComboBox?.SelectedItem is FontFamily uiFont)
        {
            UiFontPreview.FontFamily = uiFont;
            UiFontPreview.FontSize = UiFontSizeComboBox.SelectedItem as double? ?? 13;
        }

        if (LogFontComboBox?.SelectedItem is FontFamily logFont)
        {
            LogFontPreview.FontFamily = logFont;
            LogFontPreview.FontSize = LogFontSizeComboBox.SelectedItem as double? ?? 11;
            LogFontPreview.FontWeight = LogBoldToggle.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;
            LogFontPreview.FontStyle = LogItalicToggle.IsChecked == true ? FontStyles.Italic : FontStyles.Normal;
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出 XProj 配置",
            Filter = "XProj 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = $"xproj-config-{DateTime.Now:yyyyMMdd-HHmm}.json",
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunFileOperationAsync(() => _exportConfiguration(dialog.FileName), "配置已导出");
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            await _checkForUpdates(this);
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 XProj 配置",
            Filter = "XProj 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true ||
            !AppDialog.Confirm(this, "导入配置", "导入将替换当前分组、项目、命令和设置。是否继续？", "导入配置"))
        {
            return;
        }

        await RunFileOperationAsync(async () =>
        {
            _settings = await _importConfiguration(dialog.FileName);
            ApplySettingsToControls(_settings);
        }, "配置已导入并应用");
    }

    private async Task RunFileOperationAsync(Func<Task> operation, string successMessage)
    {
        ValidationText.Text = string.Empty;
        OperationStatusText.Text = string.Empty;
        try
        {
            await operation();
            OperationStatusText.Text = successMessage;
        }
        catch (Exception exception)
        {
            ValidationText.Text = exception.Message;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ThemeComboBox.SelectedIndex < 0 || CloseBehaviorComboBox.SelectedIndex < 0 ||
            UiFontComboBox.SelectedItem is not FontFamily uiFont || UiFontSizeComboBox.SelectedItem is not double uiFontSize ||
            LogFontComboBox.SelectedItem is not FontFamily logFont || LogFontSizeComboBox.SelectedItem is not double logFontSize ||
            LogVisibleLineCountComboBox.SelectedItem is not int logVisibleLineCount)
        {
            ValidationText.Text = "请完成外观和字体设置。";
            return;
        }

        Result = new AppSettings
        {
            Theme = ThemeComboBox.SelectedIndex == 1 ? "Light" : "Dark",
            CloseBehavior = CloseBehaviorComboBox.SelectedIndex == 1 ? "Exit" : "MinimizeToTray",
            UiFontFamily = uiFont.Source,
            UiFontSize = uiFontSize,
            LogFontFamily = logFont.Source,
            LogFontSize = logFontSize,
            LogFontBold = LogBoldToggle.IsChecked == true,
            LogFontItalic = LogItalicToggle.IsChecked == true,
            LogVisibleLineCount = logVisibleLineCount
        };
        DialogResult = true;
    }
}
