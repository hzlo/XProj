using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ProjectManager.Wpf.Infrastructure;
using ProjectManager.Wpf.Models;

namespace ProjectManager.Wpf.Views;

public partial class SettingsDialog : Window
{
    private readonly Func<string, Task> _exportConfiguration;
    private readonly Func<string, Task<AppSettings>> _importConfiguration;
    private readonly Func<Window, Task> _checkForUpdates;
    private readonly Action<AppSettings> _previewSettings;
    private readonly List<FontFamily> _fonts;
    private AppSettings _settings;
    private bool _isInitializing;

    public SettingsDialog(
        AppSettings settings,
        Func<string, Task> exportConfiguration,
        Func<string, Task<AppSettings>> importConfiguration,
        Action<AppSettings> previewSettings,
        string currentVersion,
        Func<Window, Task> checkForUpdates)
    {
        InitializeComponent();
        _isInitializing = true;
        _settings = settings.Clone();
        _exportConfiguration = exportConfiguration;
        _importConfiguration = importConfiguration;
        _previewSettings = previewSettings;
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
        _isInitializing = false;
    }

    public AppSettings? Result { get; private set; }

private void ApplySettingsToControls(AppSettings settings)
    {
        ThemeComboBox.SelectedIndex = settings.Theme == "Light" ? 1 : 0;
        LightForegroundTextBox.Text = settings.LightForegroundColor;
        LightBackgroundTextBox.Text = settings.LightBackgroundColor;
        DarkForegroundTextBox.Text = settings.DarkForegroundColor;
        DarkBackgroundTextBox.Text = settings.DarkBackgroundColor;
        CloseBehaviorComboBox.SelectedIndex = settings.CloseBehavior == "Exit" ? 1 : 0;
        GlobalHotkeyTextBox.Text = settings.GlobalHotkey;
        UiFontComboBox.SelectedItem = FindFont(settings.UiFontFamily);
        UiFontSizeComboBox.SelectedItem = Math.Round(settings.UiFontSize);
        LogFontComboBox.SelectedItem = FindFont(settings.LogFontFamily);
        LogFontSizeComboBox.SelectedItem = Math.Round(settings.LogFontSize);
        LogItalicToggle.IsChecked = settings.LogFontItalic;
        LogVisibleLineCountComboBox.SelectedItem = settings.LogVisibleLineCount;
        EnablePluginsToggle.IsChecked = settings.EnablePlugins;
        EnableJsonConverterToggle.IsChecked = settings.EnableJsonConverter;
        UpdatePreviews();
    }

    private void ThemeColorTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateThemeColorPickers();
        PreviewSettings();
    }

    private void SettingsSelectionChanged(object sender, SelectionChangedEventArgs e) => PreviewSettings();

    private void SettingsTextChanged(object sender, TextChangedEventArgs e) => PreviewSettings();

    private void GlobalHotkeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.None || key == Key.Tab)
        {
            return;
        }

        e.Handled = true;
        if (GlobalHotkey.TryCreateGesture(Keyboard.Modifiers, key, out var gesture, out var error))
        {
            GlobalHotkeyTextBox.Text = gesture;
            GlobalHotkeyTextBox.SelectAll();
            ValidationText.Text = string.Empty;
            return;
        }

        ValidationText.Text = error ?? "该按键不能作为全局快捷键。";
    }

    private void ClearGlobalHotkey_Click(object sender, RoutedEventArgs e) => GlobalHotkeyTextBox.Clear();

    private void UpdateThemeColorPickers()
    {
        UpdatePickerButton(LightBackgroundPickerButton, LightBackgroundTextBox?.Text);
        UpdatePickerButton(LightForegroundPickerButton, LightForegroundTextBox?.Text);
        UpdatePickerButton(DarkBackgroundPickerButton, DarkBackgroundTextBox?.Text);
        UpdatePickerButton(DarkForegroundPickerButton, DarkForegroundTextBox?.Text);
    }

    private static void UpdatePickerButton(Button? button, string? color)
    {
        if (button is null || !ThemeManager.TryNormalizeColor(color, out var normalized))
        {
            return;
        }

        button.Background = CreateColorBrush(normalized);
        button.BorderBrush = new SolidColorBrush(Colors.White);
        button.BorderThickness = new Thickness(2);
    }

    private void PickLightBackground_Click(object sender, RoutedEventArgs e) => PickColor(LightBackgroundTextBox);
    private void PickLightForeground_Click(object sender, RoutedEventArgs e) => PickColor(LightForegroundTextBox);
    private void PickDarkBackground_Click(object sender, RoutedEventArgs e) => PickColor(DarkBackgroundTextBox);
    private void PickDarkForeground_Click(object sender, RoutedEventArgs e) => PickColor(DarkForegroundTextBox);

    private void PickColor(TextBox target)
    {
        var dialog = new ColorPickerDialog(target.Text) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ResultColor is not null)
        {
            target.Text = dialog.ResultColor;
        }
    }

    private static SolidColorBrush CreateColorBrush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private void ResetLightColors_Click(object sender, RoutedEventArgs e)
    {
        LightForegroundTextBox.Text = AppSettings.DefaultLightForegroundColor;
        LightBackgroundTextBox.Text = AppSettings.DefaultLightBackgroundColor;
    }

    private void ResetDarkColors_Click(object sender, RoutedEventArgs e)
    {
        DarkForegroundTextBox.Text = AppSettings.DefaultDarkForegroundColor;
        DarkBackgroundTextBox.Text = AppSettings.DefaultDarkBackgroundColor;
    }

    private FontFamily FindFont(string source) =>
        _fonts.FirstOrDefault(font => font.Source.Equals(source, StringComparison.OrdinalIgnoreCase)) ?? _fonts.First();

    private void FontSelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdatePreviews();
        PreviewSettings();
    }

    private void PreviewSettings()
    {
        if (_isInitializing || !TryBuildSettings(validateContrast: false, out var candidate))
        {
            return;
        }

        _previewSettings(candidate);
    }

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
            _isInitializing = true;
            try
            {
                ApplySettingsToControls(_settings);
            }
            finally
            {
                _isInitializing = false;
            }
            PreviewSettings();
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
        if (!TryBuildSettings(validateContrast: true, out var settings))
        {
            return;
        }

        Result = settings;
        DialogResult = true;
    }

    private bool TryBuildSettings(bool validateContrast, out AppSettings settings)
    {
        settings = null!;
        if (!TryGetThemeColors(
                validateContrast,
                out var lightForeground,
                out var lightBackground,
                out var darkForeground,
                out var darkBackground))
        {
            return false;
        }

        if (ThemeComboBox.SelectedIndex < 0 || CloseBehaviorComboBox.SelectedIndex < 0 ||
            UiFontComboBox.SelectedItem is not FontFamily uiFont || UiFontSizeComboBox.SelectedItem is not double uiFontSize ||
            LogFontComboBox.SelectedItem is not FontFamily logFont || LogFontSizeComboBox.SelectedItem is not double logFontSize ||
            LogVisibleLineCountComboBox.SelectedItem is not int logVisibleLineCount)
        {
            if (validateContrast)
            {
                ValidationText.Text = "请完成外观和字体设置。";
            }
            return false;
        }

        if (!GlobalHotkey.TryNormalizeGesture(GlobalHotkeyTextBox.Text, out var globalHotkey, out var hotkeyError))
        {
            if (validateContrast)
            {
                ValidationText.Text = hotkeyError ?? "全局快捷键格式无效。";
            }
            return false;
        }

settings = new AppSettings
        {
            Theme = ThemeComboBox.SelectedIndex == 1 ? "Light" : "Dark",
            LightForegroundColor = lightForeground,
            LightBackgroundColor = lightBackground,
            DarkForegroundColor = darkForeground,
            DarkBackgroundColor = darkBackground,
            CloseBehavior = CloseBehaviorComboBox.SelectedIndex == 1 ? "Exit" : "MinimizeToTray",
            GlobalHotkey = globalHotkey,
            UiFontFamily = uiFont.Source,
            UiFontSize = uiFontSize,
            LogFontFamily = logFont.Source,
            LogFontSize = logFontSize,
            LogFontBold = false,
            LogFontItalic = LogItalicToggle.IsChecked == true,
            LogVisibleLineCount = logVisibleLineCount,
            EnablePlugins = EnablePluginsToggle.IsChecked == true,
            EnableNotes = _settings.EnableNotes,
            EnableWsl = _settings.EnableWsl,
            EnableTranslator = _settings.EnableTranslator,
            EnableJsonConverter = EnableJsonConverterToggle.IsChecked == true
        };
        if (validateContrast)
        {
            ValidationText.Text = string.Empty;
        }
        return true;
    }

    private bool TryGetThemeColors(
        bool showValidation,
        out string lightForeground,
        out string lightBackground,
        out string darkForeground,
        out string darkBackground)
    {
        lightForeground = string.Empty;
        lightBackground = string.Empty;
        darkForeground = string.Empty;
        darkBackground = string.Empty;
        if (!ThemeManager.TryNormalizeColor(LightForegroundTextBox.Text, out lightForeground) ||
            !ThemeManager.TryNormalizeColor(LightBackgroundTextBox.Text, out lightBackground) ||
            !ThemeManager.TryNormalizeColor(DarkForegroundTextBox.Text, out darkForeground) ||
            !ThemeManager.TryNormalizeColor(DarkBackgroundTextBox.Text, out darkBackground))
        {
            if (showValidation)
            {
                ValidationText.Text = "主题颜色请使用 #RRGGBB 格式。";
            }
            return false;
        }

        if (!ThemeManager.HasReadableContrast(lightForeground, lightBackground) ||
            !ThemeManager.HasReadableContrast(darkForeground, darkBackground))
        {
            if (showValidation)
            {
                ValidationText.Text = "前景色与背景色对比度不足，请调整后再保存。";
            }
            return false;
        }

        return true;
    }
}
