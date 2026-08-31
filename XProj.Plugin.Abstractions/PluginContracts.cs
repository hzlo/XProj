using System.Windows;

namespace XProj.Plugin.Abstractions;

public sealed class PluginHostContext
{
    public PluginHostContext(string dataDirectory, Action<string>? setStatus = null)
    {
        DataDirectory = dataDirectory;
        SetStatus = setStatus;
    }

    public string DataDirectory { get; }
    public Action<string>? SetStatus { get; }
}

public interface IXProjPlugin
{
    string Id { get; }
    string Name { get; }
    string Description { get; }

    FrameworkElement CreateView(PluginHostContext context);
}
