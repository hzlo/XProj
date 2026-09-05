using System.Windows;
using XProj.Plugin.Abstractions;

namespace XProj.Plugin.Translator;

public sealed class TranslatorPlugin : IXProjPlugin
{
    public string Id => "translator";
    public string Name => "翻译器";
    public string Description => "连接腾讯、阿里或 Google 翻译服务";
    public string Version => typeof(TranslatorPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public Material.Icons.MaterialIconKind Icon => Material.Icons.MaterialIconKind.Translate;

    public FrameworkElement CreateView(PluginHostContext context) => new TranslatorView(context);
}
