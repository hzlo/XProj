using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XProj.Plugin.Abstractions;

namespace XProj.Plugin.Translator;

public partial class TranslatorView : UserControl
{
    private readonly PluginHostContext _context;
    private readonly TranslationService _service = new();
    private readonly string[] _languages = ["auto", "中文", "English", "日本語", "한국어", "français", "Deutsch", "español", "Русский"];
    private TranslatorSettings _settings = new();
    private bool _loading;

    public TranslatorView(PluginHostContext context)
    {
        InitializeComponent();
        _context = context;
        ProviderBox.ItemsSource = new[] { "Google", "腾讯翻译", "阿里翻译" };
        SourceLanguageBox.ItemsSource = _languages;
        TargetLanguageBox.ItemsSource = _languages.Where(language => language != "auto").ToArray();
        Loaded += async (_, _) => await LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        _settings = await TranslatorSettings.LoadAsync(_context.DataDirectory);
        _loading = true;
        ProviderBox.SelectedItem = ToProviderDisplay(_settings.Provider);
        SourceLanguageBox.SelectedItem = ToDisplayLanguage(_settings.SourceLanguage);
        TargetLanguageBox.SelectedItem = ToDisplayLanguage(_settings.TargetLanguage);
        _loading = false;
    }

    private async void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.Provider = ToProviderApi(ProviderBox.SelectedItem as string);
        await _settings.SaveAsync(_context.DataDirectory);
        _context.SetStatus?.Invoke($"翻译服务已切换为{ToProviderDisplay(_settings.Provider)}。");
    }

    private void SourceTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        e.Handled = true;
        Translate_Click(sender, e);
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SourceTextBox.Text))
        {
            _context.SetStatus?.Invoke("请输入要翻译的内容。");
            SourceTextBox.Focus();
            return;
        }

        BusyOverlay.Visibility = Visibility.Visible;
        try
        {
            ResultTextBox.Text = await _service.TranslateAsync(SourceTextBox.Text, _settings);
            _context.SetStatus?.Invoke($"翻译完成（{_settings.Provider}）。");
        }
        catch (Exception exception)
        {
            ResultTextBox.Text = $"翻译失败：{exception.Message}";
            _context.SetStatus?.Invoke("翻译失败，请检查网络和服务设置。");
        }
        finally
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void SwapLanguages_Click(object sender, RoutedEventArgs e)
    {
        if (SourceLanguageBox.SelectedItem is not string source || TargetLanguageBox.SelectedItem is not string target)
        {
            return;
        }

        if (source == "auto")
        {
            return;
        }

        (SourceTextBox.Text, ResultTextBox.Text) = (ResultTextBox.Text, SourceTextBox.Text);
        SourceLanguageBox.SelectedItem = target;
        TargetLanguageBox.SelectedItem = source;
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TranslatorSettingsWindow(_settings) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _settings = dialog.Settings;
        await _settings.SaveAsync(_context.DataDirectory);
        _loading = true;
        SourceLanguageBox.SelectedItem = ToDisplayLanguage(_settings.SourceLanguage);
        TargetLanguageBox.SelectedItem = ToDisplayLanguage(_settings.TargetLanguage);
        _loading = false;
        _context.SetStatus?.Invoke("翻译服务设置已保存。");
    }

    private async void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.SourceLanguage = ToApiLanguage(SourceLanguageBox.SelectedItem as string, true);
        _settings.TargetLanguage = ToApiLanguage(TargetLanguageBox.SelectedItem as string, false);
        await _settings.SaveAsync(_context.DataDirectory);
    }

    private static string ToProviderApi(string? provider) => provider switch
    {
        "腾讯翻译" => "Tencent",
        "阿里翻译" => "Alibaba",
        _ => "Google"
    };

    private static string ToProviderDisplay(string? provider) => provider switch
    {
        "Tencent" => "腾讯翻译",
        "Alibaba" => "阿里翻译",
        _ => "Google"
    };

    private static string ToApiLanguage(string? language, bool source) => language switch
    {
        "中文" => "zh-CN",
        "English" => "en",
        "日本語" => "ja",
        "한국어" => "ko",
        "français" => "fr",
        "Deutsch" => "de",
        "español" => "es",
        "Русский" => "ru",
        _ => source ? "auto" : "zh-CN"
    };

    private static string ToDisplayLanguage(string? language) => language?.ToLowerInvariant() switch
    {
        "zh" or "zh-cn" => "中文",
        "en" or "en-us" => "English",
        "ja" => "日本語",
        "ko" => "한국어",
        "fr" => "français",
        "de" => "Deutsch",
        "es" => "español",
        "ru" => "Русский",
        _ => "auto"
    };
}
