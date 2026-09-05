using System.Windows;
using XProj.Plugin.Abstractions;

namespace XProj.Plugin.Wsl;

public sealed class WslPlugin : IXProjPlugin, IXProjPluginLifecycle
{
    public string Id => "wsl";
    public string Name => "WSL";
    public string Description => "查看 WSL 发行版并运行命令";
    public string Version => typeof(WslPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public Material.Icons.MaterialIconKind Icon => Material.Icons.MaterialIconKind.Linux;

    public FrameworkElement CreateView(PluginHostContext context) => new WslView(context);

    public Task OnShownAsync(FrameworkElement view) => ((WslView)view).InitializeAsync();

    public Task OnUnloadAsync(FrameworkElement view) => ((WslView)view).ShutdownAsync();
}
