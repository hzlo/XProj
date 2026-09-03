using System.Windows;
using System.Windows.Controls;

namespace XProj.Plugin.Translator;

public partial class TranslatorSettingsWindow : Window
{
    public TranslatorSettings Settings { get; }

    public TranslatorSettingsWindow(TranslatorSettings settings)
    {
        InitializeComponent();
        Settings = new TranslatorSettings
        {
            Provider = settings.Provider,
            SourceLanguage = settings.SourceLanguage,
            TargetLanguage = settings.TargetLanguage,
            TencentSecretId = settings.TencentSecretId,
            TencentSecretKey = settings.TencentSecretKey,
            TencentRegion = settings.TencentRegion,
            AliAccessKeyId = settings.AliAccessKeyId,
            AliAccessKeySecret = settings.AliAccessKeySecret,
            AliEndpoint = settings.AliEndpoint,
            GoogleApiKey = settings.GoogleApiKey,
            GoogleUsePublicEndpoint = settings.GoogleUsePublicEndpoint
        };
        TencentSecretIdBox.Text = Settings.TencentSecretId;
        TencentSecretKeyBox.Password = Settings.TencentSecretKey;
        TencentRegionBox.Text = Settings.TencentRegion;
        AliAccessKeyIdBox.Text = Settings.AliAccessKeyId;
        AliAccessKeySecretBox.Password = Settings.AliAccessKeySecret;
        AliEndpointBox.Text = Settings.AliEndpoint;
        GoogleApiKeyBox.Password = Settings.GoogleApiKey;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Settings.TencentSecretId = TencentSecretIdBox.Text.Trim();
        Settings.TencentSecretKey = TencentSecretKeyBox.Password;
        Settings.TencentRegion = string.IsNullOrWhiteSpace(TencentRegionBox.Text) ? "ap-beijing" : TencentRegionBox.Text.Trim();
        Settings.AliAccessKeyId = AliAccessKeyIdBox.Text.Trim();
        Settings.AliAccessKeySecret = AliAccessKeySecretBox.Password;
        Settings.AliEndpoint = string.IsNullOrWhiteSpace(AliEndpointBox.Text) ? "mt.cn-hangzhou.aliyuncs.com" : AliEndpointBox.Text.Trim();
        Settings.GoogleApiKey = GoogleApiKeyBox.Password;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
