using System.Windows;
using XProj.Plugin.Abstractions;

namespace XProj.Plugin.Wsl;

public sealed class WslPlugin : IXProjPlugin
{
    public string Id => "wsl";
    public string Name => "WSL";
    public string Description => "查看 WSL 发行版并运行命令";

    public FrameworkElement CreateView(PluginHostContext context) => new WslView(context);
}
