namespace ProjectManager.Wpf.Models;

public sealed class AppData
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<ProjectGroup> Groups { get; set; } = new();
    public List<ManagedProject> Projects { get; set; } = new();
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public string CloseBehavior { get; set; } = "MinimizeToTray";
    public string UiFontFamily { get; set; } = "Microsoft YaHei UI";
    public double UiFontSize { get; set; } = 13;
    public string LogFontFamily { get; set; } = "Consolas";
    public double LogFontSize { get; set; } = 11;
    public bool LogFontBold { get; set; }
    public bool LogFontItalic { get; set; }
    public int LogVisibleLineCount { get; set; } = 300;

    public AppSettings Clone() => new()
    {
        Theme = Theme,
        CloseBehavior = CloseBehavior,
        UiFontFamily = UiFontFamily,
        UiFontSize = UiFontSize,
        LogFontFamily = LogFontFamily,
        LogFontSize = LogFontSize,
        LogFontBold = LogFontBold,
        LogFontItalic = LogFontItalic,
        LogVisibleLineCount = LogVisibleLineCount
    };
}

public sealed class ProjectGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid? ParentId { get; set; }
    public int SortOrder { get; set; }
}

public sealed class ManagedProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public Guid? GroupId { get; set; }
    public List<ProjectCommand> Commands { get; set; } = new();
}

public sealed class ProjectCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string CommandText { get; set; } = string.Empty;
}
