namespace ProjectManager.Wpf.Infrastructure;

public sealed class LogLine
{
    public LogLine(string text)
    {
        Text = text;
    }

    public string Text { get; }
}
