namespace ProjectManager.Wpf.Models;

public sealed class AppData
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<ProjectGroup> Groups { get; set; } = new();
    public List<ManagedProject> Projects { get; set; } = new();
    public List<RunPlan> RunPlans { get; set; } = new();
}

public sealed class AppSettings
{
    public const string DefaultLightForegroundColor = "#1D1D1F";
    public const string DefaultLightBackgroundColor = "#DCDDE1";
    public const string DefaultDarkForegroundColor = "#C0C8E4";
    public const string DefaultDarkBackgroundColor = "#0E0F12";

    public string Theme { get; set; } = "Dark";
    public string LightForegroundColor { get; set; } = DefaultLightForegroundColor;
    public string LightBackgroundColor { get; set; } = DefaultLightBackgroundColor;
    public string DarkForegroundColor { get; set; } = DefaultDarkForegroundColor;
    public string DarkBackgroundColor { get; set; } = DefaultDarkBackgroundColor;
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
        LightForegroundColor = LightForegroundColor,
        LightBackgroundColor = LightBackgroundColor,
        DarkForegroundColor = DarkForegroundColor,
        DarkBackgroundColor = DarkBackgroundColor,
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
    public int SortOrder { get; set; }
    public List<ProjectCommand> Commands { get; set; } = new();
}

public sealed class ProjectCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string CommandText { get; set; } = string.Empty;
    public string Shell { get; set; } = "Cmd";
    public string EnvironmentVariables { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public sealed class RunPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool StopCommandsOutsidePlan { get; set; } = true;
    public int SortOrder { get; set; }
    public List<RunPlanCommand> Commands { get; set; } = new();
}

public sealed class RunPlanCommand
{
    public Guid ProjectId { get; set; }
    public Guid CommandId { get; set; }
    public int DelaySeconds { get; set; }
    public int SortOrder { get; set; }
}
