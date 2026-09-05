using System.Windows;
using XProj.Plugin.Abstractions;

namespace XProj.Plugin.JsonConverter;

public sealed class JsonConverterPlugin : IXProjPlugin
{
    public string Id => "json-converter";
    public string Name => "JSON 工具";
    public string Description => "格式化、排序、压缩并处理复杂 JSON";
    public string Version => typeof(JsonConverterPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public Material.Icons.MaterialIconKind Icon => Material.Icons.MaterialIconKind.CodeJson;

    public FrameworkElement CreateView(PluginHostContext context) => new JsonConverterView(context);
}
