using System.Windows;
using XProj.Plugin.Abstractions;

namespace XProj.Plugin.DataSync;

public sealed class DataSyncPlugin : IXProjPlugin
{
    public string Id => "data-sync";
    public string Name => "数据同步";
    public string Description => "使用 WebDAV 同步本机配置文件";
    public Material.Icons.MaterialIconKind Icon => Material.Icons.MaterialIconKind.CloudSyncOutline;

    public FrameworkElement CreateView(PluginHostContext context) => new DataSyncView(context);
}
