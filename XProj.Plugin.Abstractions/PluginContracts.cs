using System.Windows;

namespace XProj.Plugin.Abstractions;
public sealed class PluginHostContext
{
    public PluginHostContext(
        string dataDirectory,
        Action<string>? setStatus = null,
        Func<string, string, string, bool>? confirm = null)
    {
        DataDirectory = dataDirectory;
        SetStatus = setStatus;
        Confirm = confirm;
    }

    public string DataDirectory { get; }

    public Action<string>? SetStatus { get; }

    /// <summary>
    /// 显示宿主确认对话框。参数为标题、内容与主按钮文本，返回用户是否确认。
    /// </summary>
    public Func<string, string, string, bool>? Confirm { get; }
}

public interface IXProjPlugin
{
    string Id { get; }
    string Name { get; }
    string Description { get; }

    FrameworkElement CreateView(PluginHostContext context);
}
