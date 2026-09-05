using System.Windows;
using XProj.Plugin.Abstractions;

namespace XProj.Plugin.Notes;

public sealed class NotesPlugin : IXProjPlugin
{
    public string Id => "notes";
    public string Name => "备忘录";
    public string Description => "保存和预览全局 Markdown 笔记";
    public string Version => typeof(NotesPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public Material.Icons.MaterialIconKind Icon => Material.Icons.MaterialIconKind.NoteTextOutline;

    public FrameworkElement CreateView(PluginHostContext context) => new NotesView(context);
}
