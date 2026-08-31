using System.Windows;
using XProj.Plugin.Abstractions;

namespace XProj.Plugin.Notes;

public sealed class NotesPlugin : IXProjPlugin
{
    public string Id => "notes";
    public string Name => "备忘录";
    public string Description => "保存和预览全局 Markdown 笔记";

    public FrameworkElement CreateView(PluginHostContext context) => new NotesView(context);
}
