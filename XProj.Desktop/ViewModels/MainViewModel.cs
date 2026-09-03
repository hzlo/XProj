using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ProjectManager.Wpf.Infrastructure;
using ProjectManager.Wpf.Models;

namespace ProjectManager.Wpf.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int MaximumLogCharacters = 500_000;
    private const int RetainedLogCharacters = 400_000;
    private const int MinimumDisplayedLogCharacters = 60_000;
    private const int DisplayedCharactersPerLine = 256;
    private const int MaximumOutputEventsPerFlush = 5_000;
    private const int MaximumOutputCharactersPerFlush = 512_000;
    private static readonly TimeSpan OutputFlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly JsonDataStore _dataStore;
    private readonly ProcessManager _processManager;
    private readonly SystemLauncher _systemLauncher;
    private readonly Dictionary<Guid, RollingLogBuffer> _logs = new();
    private readonly ConcurrentDictionary<Guid, int> _logGenerations = new();
    private readonly ConcurrentDictionary<Guid, byte> _expectedProcessStops = new();
    private readonly ConcurrentQueue<PendingProcessOutput> _pendingOutput = new();
    private readonly AsyncLogFileWriter _logFileWriter;
    private LogLineBuffer _displayedLog = new(300);
    private AppData _data = new();
    private AppSettings? _previewLogSettings;
    private GroupTreeItem? _selectedGroup;
    private ManagedProject? _selectedProject;
    private CommandRuntimeViewModel? _selectedCommand;
    private string _searchText = string.Empty;
    private string _logSearchText = string.Empty;
    private FontFamily _logFontFamily = new("Consolas");
    private double _logFontSize = 11;
    private FontStyle _logFontStyle = FontStyles.Normal;
    private string _statusText = "正在加载...";
    private int _runningCount;
    private int _outputFlushScheduled;

    public MainViewModel(JsonDataStore dataStore, ProcessManager processManager, SystemLauncher systemLauncher)
    {
        _dataStore = dataStore;
        _processManager = processManager;
        _systemLauncher = systemLauncher;
        _logFileWriter = new AsyncLogFileWriter(Path.Combine(dataStore.DataDirectory, "logs"));
        _processManager.OutputReceived += ProcessManagerOnOutputReceived;
        _processManager.ProcessExited += ProcessManagerOnProcessExited;
    }

    public event EventHandler<LogDisplayUpdateEventArgs>? LogDisplayUpdated;
    public event EventHandler<AbnormalProcessExitEventArgs>? AbnormalProcessExited;

    public ObservableCollection<GroupTreeItem> GroupItems { get; } = new();
    public ObservableCollection<WorkspaceTreeItem> WorkspaceItems { get; } = new();
    public ObservableCollection<ManagedProject> Projects { get; } = new();
    public ObservableCollection<CommandRuntimeViewModel> Commands { get; } = new();
    public ObservableCollection<RunningCommandSummary> RunningCommands { get; } = new();
    public ObservableCollection<RunPlan> RunPlans { get; } = new();
    public ObservableCollection<LogLine> DisplayedLogLines { get; } = new();

    public GroupTreeItem? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
            {
                foreach (var item in EnumerateGroupTreeItems())
                {
                    item.IsSelected = ReferenceEquals(item, value);
                }
                OnPropertyChanged(nameof(CanEditSelectedGroup));
                RefreshProjects(rebuildWorkspace: false);
            }
        }
    }

    public ManagedProject? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (SetProperty(ref _selectedProject, value))
            {
                OnPropertyChanged(nameof(HasSelectedProject));
                RefreshCommands();
            }
        }
    }

    public CommandRuntimeViewModel? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            var previousCommand = _selectedCommand;
            if (SetProperty(ref _selectedCommand, value))
            {
                if (previousCommand is not null &&
                    previousCommand.Command.Id != value?.Command.Id &&
                    !_processManager.IsRunning(previousCommand.Command.Id))
                {
                    ClearCommandLog(previousCommand.Command.Id, discardPendingOutput: true);
                }

                OnPropertyChanged(nameof(HasSelectedCommand));
                RefreshLogText();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshProjects();
            }
        }
    }

    public string LogSearchText
    {
        get => _logSearchText;
        set
        {
            if (SetProperty(ref _logSearchText, value))
            {
                RefreshLogText();
            }
        }
    }

    public FontFamily LogFontFamily
    {
        get => _logFontFamily;
        private set
        {
            if (SetProperty(ref _logFontFamily, value))
            {
                OnPropertyChanged(nameof(LogFontSummary));
            }
        }
    }

    public double LogFontSize
    {
        get => _logFontSize;
        private set
        {
            if (SetProperty(ref _logFontSize, value))
            {
                OnPropertyChanged(nameof(LogFontSummary));
            }
        }
    }

    public FontStyle LogFontStyle
    {
        get => _logFontStyle;
        private set => SetProperty(ref _logFontStyle, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public int RunningCount
    {
        get => _runningCount;
        private set
        {
            if (SetProperty(ref _runningCount, value))
            {
                OnPropertyChanged(nameof(HasRunningCommands));
            }
        }
    }

    public bool HasRunningCommands => RunningCount > 0;

    public bool CanEditSelectedGroup => SelectedGroup?.Kind == GroupFilterKind.Group;
    public bool HasSelectedProject => SelectedProject is not null;
    public bool HasSelectedCommand => SelectedCommand is not null;
    public string DataDirectory => _dataStore.DataDirectory;
    public string DataFilePath => _dataStore.DataFilePath;
    public string LogFontSummary => $"{LogFontFamily.Source} · {LogFontSize * 72 / 96:0.##} pt";
    public bool IsLogFontItalic => LogFontStyle == FontStyles.Italic;
    public AppSettings CurrentSettings => _data.Settings.Clone();
public bool EnablePlugins => _data.Settings.EnablePlugins;
    public bool EnableNotes => _data.Settings.EnableNotes;
    public bool EnableWsl => _data.Settings.EnableWsl;
    public bool EnableTranslator => _data.Settings.EnableTranslator;
    public bool EnableJsonConverter => _data.Settings.EnableJsonConverter;
    public bool EnableDataSync => _data.Settings.EnableDataSync;

    public void SetStatus(string text) => StatusText = text;

    public async Task InitializeAsync()
    {
        _data = await _dataStore.LoadAsync();
        _previewLogSettings = null;
        ApplyLogSettings(_data.Settings);
        NormalizeGroupHierarchy();
        NormalizeSortOrders();
        RebuildGroupTree(GroupFilterKind.All, null);
        RefreshRunPlans();
        StatusText = $"已加载 {_data.Projects.Count} 个项目";
    }

    public IReadOnlyList<GroupChoice> GetGroupChoices(Guid? excludedGroupId = null)
    {
        var choices = new List<GroupChoice> { new(null, "未分组") };
        var excludedIds = excludedGroupId.HasValue
            ? GetDescendantGroupIds(excludedGroupId.Value).Append(excludedGroupId.Value).ToHashSet()
            : new HashSet<Guid>();

        foreach (var group in _data.Groups.Where(item => item.ParentId is null).OrderBy(item => item.SortOrder).ThenBy(item => item.Name))
        {
            AddGroupChoice(choices, group, 0, excludedIds);
        }

        return choices;
    }

    public async Task AddGroupAsync(string name, Guid? parentId)
    {
        ValidateGroupName(name, null);
        ValidateParentGroup(parentId, null);
        var group = new ProjectGroup
        {
            Name = name.Trim(),
            ParentId = parentId,
            SortOrder = NextGroupSortOrder(parentId)
        };
        _data.Groups.Add(group);
        await SaveAndRefreshAsync(GroupFilterKind.Group, group.Id);
    }

    public async Task UpdateGroupAsync(Guid groupId, string name, Guid? parentId)
    {
        var group = _data.Groups.Single(item => item.Id == groupId);
        ValidateGroupName(name, groupId);
        ValidateParentGroup(parentId, groupId);
        group.Name = name.Trim();
        group.ParentId = parentId;
        group.SortOrder = NextGroupSortOrder(parentId, groupId);
        await SaveAndRefreshAsync(GroupFilterKind.Group, group.Id);
    }

    public async Task DeleteGroupAsync(Guid groupId)
    {
        var group = _data.Groups.Single(item => item.Id == groupId);
        foreach (var child in _data.Groups.Where(item => item.ParentId == groupId))
        {
            child.ParentId = group.ParentId;
        }

        foreach (var project in _data.Projects.Where(item => item.GroupId == groupId))
        {
            project.GroupId = group.ParentId;
        }

        _data.Groups.Remove(group);
        await SaveAndRefreshAsync(
            group.ParentId.HasValue ? GroupFilterKind.Group : GroupFilterKind.All,
            group.ParentId);
    }

    public async Task AddProjectAsync(ManagedProject project)
    {
        ValidateProject(project, null);
        project.Id = project.Id == Guid.Empty ? Guid.NewGuid() : project.Id;
        project.SortOrder = NextProjectSortOrder();
        EnsureCommandIds(project);
        _data.Projects.Add(project);
        await _dataStore.SaveAsync(_data);
        RefreshProjects(project.Id);
        StatusText = $"已添加项目：{project.Name}";
    }

    public async Task UpdateProjectAsync(ManagedProject project)
    {
        ValidateProject(project, project.Id);
        if (_processManager.HasRunningCommands(project.Id))
        {
            throw new InvalidOperationException("项目仍有命令正在运行，请先停止后再编辑。");
        }

        EnsureCommandIds(project);
        var index = _data.Projects.FindIndex(item => item.Id == project.Id);
        if (index < 0)
        {
            throw new InvalidOperationException("项目不存在或已被删除。");
        }

        project.SortOrder = _data.Projects[index].SortOrder;
        _data.Projects[index] = project;
        await _dataStore.SaveAsync(_data);
        RefreshProjects(project.Id);
        StatusText = $"已更新项目：{project.Name}";
    }

    public async Task DeleteProjectAsync(Guid projectId)
    {
        var commandIds = _data.Projects
            .Where(item => item.Id == projectId)
            .SelectMany(item => item.Commands)
            .Select(item => item.Id)
            .ToArray();
        await _processManager.StopProjectAsync(projectId);
        foreach (var commandId in commandIds)
        {
            _logs.Remove(commandId);
            _logGenerations.TryRemove(commandId, out _);
        }
        _data.Projects.RemoveAll(item => item.Id == projectId);
        await _dataStore.SaveAsync(_data);
        RefreshProjects();
        UpdateRunningCount();
        StatusText = "项目已删除";
    }

    public async Task RunCommandAsync(CommandRuntimeViewModel commandRuntime)
    {
        var project = SelectedProject ?? throw new InvalidOperationException("请先选择项目。");
        SelectedCommand = commandRuntime;
        ClearCommandLog(commandRuntime.Command.Id, discardPendingOutput: true);
        commandRuntime.SetRunning(true, "启动中");
        AppendLog(commandRuntime.Command.Id, $"[{DateTime.Now:HH:mm:ss}] 正在启动 {commandRuntime.Command.Name}...");

        try
        {
            await _processManager.StartAsync(project, commandRuntime.Command);
            var isRunning = _processManager.IsRunning(commandRuntime.Command.Id);
            commandRuntime.SetRunning(isRunning, isRunning ? "运行中" : "已退出");
        }
        catch
        {
            commandRuntime.SetRunning(false, "启动失败");
            throw;
        }
        finally
        {
            UpdateRunningCount();
        }
    }

    public async Task StopCommandAsync(CommandRuntimeViewModel commandRuntime)
    {
        _expectedProcessStops[commandRuntime.Command.Id] = 0;
        commandRuntime.SetRunning(true, "停止中");
        await _processManager.StopAsync(commandRuntime.Command.Id);
        commandRuntime.SetRunning(false, "已停止");
        UpdateRunningCount();
    }

    public async Task StopCommandAsync(Guid commandId)
    {
        var command = Commands.FirstOrDefault(item => item.Command.Id == commandId);
        if (command is not null)
        {
            await StopCommandAsync(command);
            return;
        }

        _expectedProcessStops[commandId] = 0;
        await _processManager.StopAsync(commandId);
        UpdateRunningCount();
    }

    public async Task StopAllCommandsAsync()
    {
        foreach (var commandId in _processManager.RunningCommandIds)
        {
            _expectedProcessStops[commandId] = 0;
        }

        if (RunningCount == 0)
        {
            return;
        }

        StatusText = "正在停止整套环境...";
        await _processManager.StopAllAsync();
        foreach (var command in Commands)
        {
            command.SetRunning(false, "已停止");
        }

        UpdateRunningCount();
        StatusText = "整套环境已停止";
    }

    public async Task RestartCommandAsync(CommandRuntimeViewModel commandRuntime)
    {
        _expectedProcessStops[commandRuntime.Command.Id] = 0;
        await _processManager.StopAsync(commandRuntime.Command.Id);
        commandRuntime.SetRunning(false, "正在重启");
        await RunCommandAsync(commandRuntime);
    }

    public async Task UpdateCommandAsync(
        Guid commandId,
        string name,
        string commandText,
        string shell,
        string environmentVariables)
    {
        var project = SelectedProject ?? throw new InvalidOperationException("请先选择项目。");
        var command = project.Commands.SingleOrDefault(item => item.Id == commandId)
            ?? throw new InvalidOperationException("命令不存在或已被删除。");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(commandText))
        {
            throw new InvalidOperationException("命令名称和命令内容不能为空。");
        }

        command.Name = name.Trim();
        command.CommandText = commandText.Trim();
        command.Shell = shell is "PowerShell" ? "PowerShell" : "Cmd";
        command.EnvironmentVariables = environmentVariables.Trim();
        await _dataStore.SaveAsync(_data);
        RefreshCommands(commandId);
        StatusText = $"已更新命令：{command.Name}";
    }

    public async Task DeleteCommandAsync(Guid commandId)
    {
        var project = SelectedProject ?? throw new InvalidOperationException("请先选择项目。");
        var command = project.Commands.SingleOrDefault(item => item.Id == commandId)
            ?? throw new InvalidOperationException("命令不存在或已被删除。");

        await _processManager.StopAsync(commandId);
        project.Commands.Remove(command);
        _logs.Remove(commandId);
        _logGenerations.TryRemove(commandId, out _);
        await _dataStore.SaveAsync(_data);
        RefreshCommands();
        UpdateRunningCount();
        StatusText = $"已删除命令：{command.Name}";
    }

    public async Task UpdateSettingsAsync(AppSettings settings)
    {
        ValidateSettings(settings);
        _previewLogSettings = null;
        _data.Settings = settings.Clone();
        ApplyLogSettings(_data.Settings);
        RefreshLogText();
        await _dataStore.SaveAsync(_data);
        StatusText = "设置已保存";
    }

    public void PreviewLogSettings(AppSettings settings)
    {
        ValidateSettings(settings);
        _previewLogSettings = settings.Clone();
        ApplyLogSettings(_previewLogSettings);
        RefreshLogText();
    }

    public void RestoreLogSettingsPreview()
    {
        _previewLogSettings = null;
        ApplyLogSettings(_data.Settings);
        RefreshLogText();
    }

    public Task ExportConfigurationAsync(string filePath) => _dataStore.ExportAsync(_data, filePath);

    public async Task<AppSettings> ImportConfigurationAsync(string filePath)
    {
        if (RunningCount > 0)
        {
            throw new InvalidOperationException("请先停止所有正在运行的命令，再导入配置。");
        }

        var importedData = await _dataStore.ImportAsync(filePath);
        ValidateSettings(importedData.Settings);
        _data = importedData;
        _previewLogSettings = null;
        ApplyLogSettings(_data.Settings);
        NormalizeGroupHierarchy();
        NormalizeSortOrders();
        RebuildGroupTree(GroupFilterKind.All, null);
        RefreshRunPlans();
        await _dataStore.SaveAsync(_data);
        StatusText = $"已导入 {_data.Projects.Count} 个项目";
        return CurrentSettings;
    }

    public IReadOnlyList<RunPlan> GetRunPlansSnapshot() =>
        _data.RunPlans
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(CloneRunPlan)
            .ToList();

    public IReadOnlyList<RunPlanCommandChoice> GetRunPlanCommandChoices(RunPlan? runPlan)
    {
        var selectedByCommandId = (runPlan?.Commands ?? new List<RunPlanCommand>())
            .GroupBy(item => item.CommandId)
            .ToDictionary(item => item.Key, item => item.First());
        var groupNamesById = _data.Groups.ToDictionary(item => item.Id, item => item.Name);
        var choices = new List<RunPlanCommandChoice>();

        foreach (var project in _data.Projects
                     .OrderBy(item => item.SortOrder)
                     .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var projectPrefix = project.GroupId.HasValue && groupNamesById.TryGetValue(project.GroupId.Value, out var groupName)
                ? $"{groupName}-{project.Name}"
                : project.Name;
            foreach (var command in project.Commands
                         .OrderBy(item => item.SortOrder)
                         .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                selectedByCommandId.TryGetValue(command.Id, out var selected);
                choices.Add(new RunPlanCommandChoice(
                    project.Id,
                    command.Id,
                    projectPrefix,
                    command.Name,
                    selected is not null,
                    selected?.DelaySeconds ?? 0));
            }
        }

        return choices;
    }

    public async Task ReplaceRunPlansAsync(IEnumerable<RunPlan> runPlans)
    {
        var plans = runPlans.Select(CloneRunPlan).ToList();
        ValidateRunPlans(plans);
        AssignSortOrders(plans);
        foreach (var plan in plans)
        {
            AssignSortOrders(plan.Commands);
        }

        _data.RunPlans = plans;
        await _dataStore.SaveAsync(_data);
        RefreshRunPlans();
        StatusText = $"已保存 {_data.RunPlans.Count} 个运行方案";
    }

    public async Task RunPlanAsync(Guid runPlanId)
    {
        var plan = _data.RunPlans.SingleOrDefault(item => item.Id == runPlanId)
            ?? throw new InvalidOperationException("运行方案不存在或已被删除。");
        var commandRefs = plan.Commands
            .OrderBy(item => item.SortOrder)
            .ToList();
        if (commandRefs.Count == 0)
        {
            throw new InvalidOperationException("运行方案中还没有选择命令。");
        }

        var selectedCommandIds = commandRefs.Select(item => item.CommandId).ToHashSet();
        if (plan.StopCommandsOutsidePlan)
        {
            var commandsToStop = _processManager.RunningCommandIds
                .Where(commandId => !selectedCommandIds.Contains(commandId))
                .ToArray();
            await Task.WhenAll(commandsToStop.Select(_processManager.StopAsync));
            UpdateRunningCount();
        }

        foreach (var commandRef in commandRefs)
        {
            if (commandRef.DelaySeconds > 0)
            {
                StatusText = $"等待 {commandRef.DelaySeconds} 秒后继续启动方案：{plan.Name}";
                await Task.Delay(TimeSpan.FromSeconds(commandRef.DelaySeconds));
            }

            var project = _data.Projects.SingleOrDefault(item => item.Id == commandRef.ProjectId);
            var command = project?.Commands.SingleOrDefault(item => item.Id == commandRef.CommandId);
            if (project is null || command is null || _processManager.IsRunning(commandRef.CommandId))
            {
                continue;
            }

            SelectedProject = project;
            var runtime = Commands.FirstOrDefault(item => item.Command.Id == command.Id);
            if (runtime is not null)
            {
                await RunCommandAsync(runtime);
            }
        }

        UpdateRunningCount();
        StatusText = $"已启动运行方案：{plan.Name}";
    }

    public void RefreshRunningSummaries() => UpdateRunningCount();

    public void SelectWorkspaceItem(WorkspaceTreeItem item)
    {
        if (item.Kind == WorkspaceTreeItemKind.Project && item.Project is not null)
        {
            SelectedProject = item.Project;
            return;
        }

        var kind = item.Kind switch
        {
            WorkspaceTreeItemKind.All => GroupFilterKind.All,
            WorkspaceTreeItemKind.Ungrouped => GroupFilterKind.Ungrouped,
            _ => GroupFilterKind.Group
        };
        SelectedGroup = FindGroupTreeItem(kind, item.GroupId);
    }

    public async Task ReorderGroupAsync(Guid sourceGroupId, Guid targetGroupId, bool insertAfter)
    {
        if (sourceGroupId == targetGroupId)
        {
            return;
        }

        var source = _data.Groups.SingleOrDefault(item => item.Id == sourceGroupId);
        var target = _data.Groups.SingleOrDefault(item => item.Id == targetGroupId);
        if (source is null || target is null || source.ParentId != target.ParentId)
        {
            return;
        }

        var siblings = _data.Groups
            .Where(item => item.ParentId == source.ParentId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        MoveBeforeOrAfter(siblings, source, target, insertAfter);
        AssignSortOrders(siblings);
        await _dataStore.SaveAsync(_data);
        RebuildGroupTree(GroupFilterKind.Group, sourceGroupId);
        StatusText = "分组顺序已保存";
    }

    public async Task ReorderProjectAsync(Guid sourceProjectId, Guid targetProjectId, bool insertAfter)
    {
        if (sourceProjectId == targetProjectId)
        {
            return;
        }

        var source = _data.Projects.SingleOrDefault(item => item.Id == sourceProjectId);
        var target = _data.Projects.SingleOrDefault(item => item.Id == targetProjectId);
        if (source is null || target is null)
        {
            return;
        }

        var projects = _data.Projects
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        MoveBeforeOrAfter(projects, source, target, insertAfter);
        AssignSortOrders(projects);
        await _dataStore.SaveAsync(_data);
        RefreshProjects(sourceProjectId);
        StatusText = "项目顺序已保存";
    }

    public async Task ReorderCommandAsync(Guid sourceCommandId, Guid targetCommandId, bool insertAfter)
    {
        if (sourceCommandId == targetCommandId || SelectedProject is null)
        {
            return;
        }

        var source = SelectedProject.Commands.SingleOrDefault(item => item.Id == sourceCommandId);
        var target = SelectedProject.Commands.SingleOrDefault(item => item.Id == targetCommandId);
        if (source is null || target is null)
        {
            return;
        }

        var commands = SelectedProject.Commands
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        MoveBeforeOrAfter(commands, source, target, insertAfter);
        AssignSortOrders(commands);
        SelectedProject.Commands = commands;
        await _dataStore.SaveAsync(_data);
        RefreshCommands(sourceCommandId);
        StatusText = "命令顺序已保存";
    }

    public void ClearSelectedLog()
    {
        if (SelectedCommand is null)
        {
            return;
        }

        ClearCommandLog(SelectedCommand.Command.Id, discardPendingOutput: true);
    }

    public void RefreshLogDisplay() => RefreshLogText();

    public void OpenSelectedProjectFolder()
    {
        var project = SelectedProject ?? throw new InvalidOperationException("请先选择项目。");
        _systemLauncher.OpenFolder(project.WorkingDirectory);
    }

    public void OpenSelectedProjectTerminal()
    {
        var project = SelectedProject ?? throw new InvalidOperationException("请先选择项目。");
        _systemLauncher.OpenTerminal(project.WorkingDirectory);
    }

    public void OpenSelectedProjectEditor()
    {
        var project = SelectedProject ?? throw new InvalidOperationException("请先选择项目。");
        _systemLauncher.OpenInEditor(project.WorkingDirectory);
    }

    public async Task SendInputAsync(string text)
    {
        if (SelectedCommand is not { IsRunning: true } command || string.IsNullOrEmpty(text))
        {
            return;
        }

        await _processManager.SendInputAsync(command.Command.Id, text);
    }

    public async Task ShutdownAsync()
    {
        foreach (var commandId in _processManager.RunningCommandIds)
        {
            _expectedProcessStops[commandId] = 0;
        }

        StatusText = "正在停止运行中的命令...";
        await _processManager.StopAllAsync();
        FlushPendingOutput();
        await _logFileWriter.DisposeAsync();
        await _dataStore.SaveAsync(_data);
    }

    private void RefreshProjects(Guid? projectToSelect = null, bool rebuildWorkspace = true)
    {
        if (_data is null)
        {
            return;
        }

        var selectedProjectId = projectToSelect ?? SelectedProject?.Id;
        IEnumerable<ManagedProject> query = _data.Projects;
        if (SelectedGroup is not null)
        {
            query = SelectedGroup.Kind switch
            {
                GroupFilterKind.Ungrouped => query.Where(item => item.GroupId is null),
                GroupFilterKind.Group when SelectedGroup.GroupId.HasValue =>
                    query.Where(item => item.GroupId.HasValue &&
                        GetDescendantGroupIds(SelectedGroup.GroupId.Value)
                            .Append(SelectedGroup.GroupId.Value)
                            .Contains(item.GroupId.Value)),
                _ => query
            };
        }

        var search = SearchText.Trim();
        if (search.Length > 0)
        {
            query = query.Where(item =>
                item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.WorkingDirectory.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.Commands.Any(command =>
                    command.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    command.CommandText.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        var result = query
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Projects.Clear();
        foreach (var project in result)
        {
            Projects.Add(project);
        }

        SelectedProject = Projects.FirstOrDefault(item => item.Id == selectedProjectId) ?? Projects.FirstOrDefault();
        if (rebuildWorkspace)
        {
            RebuildWorkspaceTree();
        }
        StatusText = $"当前显示 {Projects.Count} / {_data.Projects.Count} 个项目";
    }

    private void RefreshCommands(Guid? commandToSelect = null)
    {
        var selectedCommandId = commandToSelect ?? SelectedCommand?.Command.Id;
        Commands.Clear();
        if (SelectedProject is not null)
        {
            foreach (var command in SelectedProject.Commands
                         .OrderBy(item => item.SortOrder)
                         .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Commands.Add(new CommandRuntimeViewModel(
                    command,
                    _processManager.IsRunning(command.Id),
                    _processManager.IsRunning(command.Id) ? "运行中" : "未运行"));
            }
        }

        SelectedCommand = Commands.FirstOrDefault(item => item.Command.Id == selectedCommandId) ?? Commands.FirstOrDefault();
    }

    private void RefreshRunPlans()
    {
        RunPlans.Clear();
        foreach (var runPlan in _data.RunPlans
                     .OrderBy(item => item.SortOrder)
                     .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            RunPlans.Add(runPlan);
        }
    }

    private async Task SaveAndRefreshAsync(GroupFilterKind kind, Guid? groupId)
    {
        await _dataStore.SaveAsync(_data);
        RebuildGroupTree(kind, groupId);
        StatusText = "分组已保存";
    }

    private void RebuildGroupTree(GroupFilterKind preferredKind, Guid? preferredGroupId)
    {
        GroupItems.Clear();
        GroupItems.Add(new GroupTreeItem("全部项目", GroupFilterKind.All, null));
        GroupItems.Add(new GroupTreeItem("未分组", GroupFilterKind.Ungrouped, null));

        var roots = _data.Groups
            .Where(item => item.ParentId is null)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (var root in roots)
        {
            GroupItems.Add(BuildGroupTreeItem(root, new HashSet<Guid>()));
        }

        SelectedGroup = FindGroupTreeItem(preferredKind, preferredGroupId) ?? GroupItems.FirstOrDefault();
        RebuildWorkspaceTree();
    }

    private void RebuildWorkspaceTree()
    {
        WorkspaceItems.Clear();
        var search = SearchText.Trim();
        var visibleProjects = _data.Projects
            .Where(project => search.Length == 0 || MatchesProject(project, search))
            .OrderBy(project => project.SortOrder)
            .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        WorkspaceItems.Add(new WorkspaceTreeItem("全部项目", WorkspaceTreeItemKind.All, projects: visibleProjects));
        WorkspaceItems.Add(new WorkspaceTreeItem(
            "未分组",
            WorkspaceTreeItemKind.Ungrouped,
            projects: visibleProjects.Where(project => project.GroupId is null).ToList()));

        foreach (var group in _data.Groups
                     .Where(item => item.ParentId is null)
                     .OrderBy(item => item.SortOrder)
                     .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = BuildWorkspaceTreeItem(group, visibleProjects, new HashSet<Guid>());
            if (item is not null)
            {
                WorkspaceItems.Add(item);
            }
        }

        foreach (var item in EnumerateWorkspaceTreeItems())
        {
            if (item.Kind == WorkspaceTreeItemKind.Project && item.Project is not null)
            {
                item.UpdateRuntimeState(item.Project.Commands.Count(command => _processManager.IsRunning(command.Id)));
            }
            item.IsSelected = item.Kind == WorkspaceTreeItemKind.Project
                ? item.Project?.Id == SelectedProject?.Id
                : item.GroupId == SelectedGroup?.GroupId && item.Kind switch
                {
                    WorkspaceTreeItemKind.Group => SelectedGroup?.Kind == GroupFilterKind.Group,
                    WorkspaceTreeItemKind.All => SelectedGroup?.Kind == GroupFilterKind.All,
                    WorkspaceTreeItemKind.Ungrouped => SelectedGroup?.Kind == GroupFilterKind.Ungrouped,
                    _ => false
                };
        }
    }

    private WorkspaceTreeItem? BuildWorkspaceTreeItem(
        ProjectGroup group,
        IReadOnlyList<ManagedProject> visibleProjects,
        HashSet<Guid> ancestors)
    {
        if (!ancestors.Add(group.Id))
        {
            return null;
        }

        var item = new WorkspaceTreeItem(group.Name, WorkspaceTreeItemKind.Group, group.Id);
        foreach (var project in visibleProjects.Where(candidate => candidate.GroupId == group.Id))
        {
            item.Children.Add(new WorkspaceTreeItem(project.Name, WorkspaceTreeItemKind.Project, project: project));
        }

        foreach (var child in _data.Groups
                     .Where(candidate => candidate.ParentId == group.Id)
                     .OrderBy(candidate => candidate.SortOrder)
                     .ThenBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var childItem = BuildWorkspaceTreeItem(child, visibleProjects, new HashSet<Guid>(ancestors));
            if (childItem is not null)
            {
                item.Children.Add(childItem);
            }
        }

        item.SetProjectCount(item.Children.Sum(child => child.Kind == WorkspaceTreeItemKind.Project
            ? 1
            : child.ProjectCount));

        return item;
    }

    private static bool MatchesProject(ManagedProject project, string search) =>
        project.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        project.WorkingDirectory.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        project.Commands.Any(command =>
            command.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            command.CommandText.Contains(search, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<WorkspaceTreeItem> EnumerateWorkspaceTreeItems()
    {
        var pending = new Stack<WorkspaceTreeItem>(WorkspaceItems.Reverse());
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            foreach (var child in current.Children.Reverse())
            {
                pending.Push(child);
            }
        }
    }

    private GroupTreeItem BuildGroupTreeItem(ProjectGroup group, HashSet<Guid> ancestors)
    {
        var item = new GroupTreeItem(group.Name, GroupFilterKind.Group, group.Id);
        if (!ancestors.Add(group.Id))
        {
            return item;
        }

        foreach (var child in _data.Groups
                     .Where(candidate => candidate.ParentId == group.Id)
                     .OrderBy(candidate => candidate.SortOrder)
                     .ThenBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            item.Children.Add(BuildGroupTreeItem(child, new HashSet<Guid>(ancestors)));
        }

        return item;
    }

    private GroupTreeItem? FindGroupTreeItem(GroupFilterKind kind, Guid? groupId)
    {
        foreach (var item in GroupItems)
        {
            var match = FindGroupTreeItem(item, kind, groupId);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private IEnumerable<GroupTreeItem> EnumerateGroupTreeItems()
    {
        var pending = new Stack<GroupTreeItem>(GroupItems.Reverse());
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            foreach (var child in current.Children.Reverse())
            {
                pending.Push(child);
            }
        }
    }

    private static GroupTreeItem? FindGroupTreeItem(GroupTreeItem item, GroupFilterKind kind, Guid? groupId)
    {
        if (item.Kind == kind && item.GroupId == groupId)
        {
            return item;
        }

        foreach (var child in item.Children)
        {
            var match = FindGroupTreeItem(child, kind, groupId);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private IEnumerable<Guid> GetDescendantGroupIds(Guid groupId)
    {
        var pending = new Stack<Guid>();
        var visited = new HashSet<Guid>();
        pending.Push(groupId);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var child in _data.Groups.Where(item => item.ParentId == current))
            {
                if (visited.Add(child.Id))
                {
                    yield return child.Id;
                    pending.Push(child.Id);
                }
            }
        }
    }

    private void AddGroupChoice(List<GroupChoice> choices, ProjectGroup group, int depth, HashSet<Guid> excludedIds)
    {
        if (excludedIds.Contains(group.Id))
        {
            return;
        }

        choices.Add(new GroupChoice(group.Id, $"{new string('　', depth)}{group.Name}"));
        foreach (var child in _data.Groups
                     .Where(item => item.ParentId == group.Id)
                     .OrderBy(item => item.SortOrder)
                     .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            AddGroupChoice(choices, child, depth + 1, excludedIds);
        }
    }

    private void NormalizeGroupHierarchy()
    {
        var groupIds = _data.Groups.Select(item => item.Id).ToHashSet();
        foreach (var group in _data.Groups)
        {
            if (group.ParentId == group.Id || group.ParentId.HasValue && !groupIds.Contains(group.ParentId.Value))
            {
                group.ParentId = null;
            }

            var seen = new HashSet<Guid> { group.Id };
            var parentId = group.ParentId;
            while (parentId.HasValue)
            {
                if (!seen.Add(parentId.Value))
                {
                    group.ParentId = null;
                    break;
                }

                parentId = _data.Groups.FirstOrDefault(item => item.Id == parentId.Value)?.ParentId;
            }
        }
    }

    private void NormalizeSortOrders()
    {
        foreach (var groupSet in _data.Groups.GroupBy(item => item.ParentId))
        {
            AssignSortOrders(OrderByStoredOrder(groupSet));
        }

        AssignSortOrders(OrderByStoredOrder(_data.Projects));

        foreach (var project in _data.Projects)
        {
            AssignSortOrders(OrderByStoredOrder(project.Commands));
        }

        AssignSortOrders(OrderByStoredOrder(_data.RunPlans));
        foreach (var runPlan in _data.RunPlans)
        {
            AssignSortOrders(OrderByStoredOrder(runPlan.Commands));
        }
    }

    private void ValidateGroupName(string name, Guid? currentGroupId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("分组名称不能为空。");
        }

        if (_data.Groups.Any(item => item.Id != currentGroupId &&
            string.Equals(item.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new InvalidOperationException("已存在同名分组。");
        }
    }

    private void ValidateParentGroup(Guid? parentId, Guid? currentGroupId)
    {
        if (!parentId.HasValue)
        {
            return;
        }

        if (_data.Groups.All(item => item.Id != parentId.Value))
        {
            throw new InvalidOperationException("父分组不存在。");
        }

        if (currentGroupId.HasValue &&
            GetDescendantGroupIds(currentGroupId.Value).Append(currentGroupId.Value).Contains(parentId.Value))
        {
            throw new InvalidOperationException("不能将分组移动到自身或其子分组下。");
        }
    }

    private void ValidateProject(ManagedProject project, Guid? currentProjectId)
    {
        if (string.IsNullOrWhiteSpace(project.Name))
        {
            throw new InvalidOperationException("项目名称不能为空。");
        }

        if (!Directory.Exists(project.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"工作目录不存在：{project.WorkingDirectory}");
        }

        if (project.GroupId.HasValue && _data.Groups.All(item => item.Id != project.GroupId.Value))
        {
            throw new InvalidOperationException("所选分组不存在。");
        }

        if (_data.Projects.Any(item => item.Id != currentProjectId &&
            string.Equals(item.WorkingDirectory.TrimEnd('\\'), project.WorkingDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("该目录已经添加过。");
        }

        if (project.Commands.Any(item => string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.CommandText)))
        {
            throw new InvalidOperationException("命令名称和命令内容不能为空。");
        }
    }

    private int NextGroupSortOrder(Guid? parentId, Guid? excludingGroupId = null) =>
        _data.Groups
            .Where(item => item.ParentId == parentId && item.Id != excludingGroupId)
            .Select(item => item.SortOrder)
            .DefaultIfEmpty(-1)
            .Max() + 1;

    private int NextProjectSortOrder() =>
        _data.Projects
            .Select(item => item.SortOrder)
            .DefaultIfEmpty(-1)
            .Max() + 1;

    private static void EnsureCommandIds(ManagedProject project)
    {
        for (var index = 0; index < project.Commands.Count; index++)
        {
            var command = project.Commands[index];
            if (command.Id == Guid.Empty)
            {
                command.Id = Guid.NewGuid();
            }

            command.Name = command.Name.Trim();
            command.CommandText = command.CommandText.Trim();
            command.Shell = command.Shell is "PowerShell" ? "PowerShell" : "Cmd";
            command.EnvironmentVariables = command.EnvironmentVariables.Trim();
            command.SortOrder = index;
        }

        project.Name = project.Name.Trim();
        project.WorkingDirectory = Path.GetFullPath(project.WorkingDirectory.Trim());
    }

    private static void MoveBeforeOrAfter<T>(List<T> items, T source, T target, bool insertAfter)
    {
        items.Remove(source);
        var targetIndex = items.IndexOf(target);
        if (targetIndex < 0)
        {
            items.Add(source);
            return;
        }

        items.Insert(insertAfter ? targetIndex + 1 : targetIndex, source);
    }

    private static List<T> OrderByStoredOrder<T>(IEnumerable<T> items)
        where T : class
    {
        return items
            .Select((item, index) => new
            {
                Item = item,
                Index = index,
                SortOrder = item switch
                {
                    ProjectGroup group => group.SortOrder,
                    ManagedProject project => project.SortOrder,
                    ProjectCommand command => command.SortOrder,
                    RunPlan runPlan => runPlan.SortOrder,
                    RunPlanCommand runPlanCommand => runPlanCommand.SortOrder,
                    _ => index
                }
            })
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Index)
            .Select(item => item.Item)
            .ToList();
    }

    private static void AssignSortOrders(IReadOnlyList<ProjectGroup> groups)
    {
        for (var index = 0; index < groups.Count; index++)
        {
            groups[index].SortOrder = index;
        }
    }

    private static void AssignSortOrders(IReadOnlyList<ManagedProject> projects)
    {
        for (var index = 0; index < projects.Count; index++)
        {
            projects[index].SortOrder = index;
        }
    }

    private static void AssignSortOrders(IReadOnlyList<ProjectCommand> commands)
    {
        for (var index = 0; index < commands.Count; index++)
        {
            commands[index].SortOrder = index;
        }
    }

    private static void AssignSortOrders(IReadOnlyList<RunPlan> runPlans)
    {
        for (var index = 0; index < runPlans.Count; index++)
        {
            runPlans[index].SortOrder = index;
        }
    }

    private static void AssignSortOrders(IReadOnlyList<RunPlanCommand> commands)
    {
        for (var index = 0; index < commands.Count; index++)
        {
            commands[index].SortOrder = index;
        }
    }

    private void ValidateRunPlans(IReadOnlyList<RunPlan> runPlans)
    {
        var commandIdsByProjectId = _data.Projects.ToDictionary(
            project => project.Id,
            project => project.Commands.Select(command => command.Id).ToHashSet());
        foreach (var runPlan in runPlans)
        {
            if (string.IsNullOrWhiteSpace(runPlan.Name))
            {
                throw new InvalidOperationException("运行方案名称不能为空。");
            }

            runPlan.Name = runPlan.Name.Trim();
            var uniqueCommandIds = new HashSet<Guid>();
            foreach (var commandRef in runPlan.Commands)
            {
                if (!commandIdsByProjectId.TryGetValue(commandRef.ProjectId, out var commandIds) ||
                    !commandIds.Contains(commandRef.CommandId))
                {
                    throw new InvalidOperationException($"运行方案“{runPlan.Name}”包含已不存在的命令。");
                }

                if (!uniqueCommandIds.Add(commandRef.CommandId))
                {
                    throw new InvalidOperationException($"运行方案“{runPlan.Name}”中存在重复命令。");
                }

                commandRef.DelaySeconds = Math.Clamp(commandRef.DelaySeconds, 0, 300);
            }
        }
    }

    private static RunPlan CloneRunPlan(RunPlan runPlan) => new()
    {
        Id = runPlan.Id == Guid.Empty ? Guid.NewGuid() : runPlan.Id,
        Name = runPlan.Name,
        StopCommandsOutsidePlan = runPlan.StopCommandsOutsidePlan,
        SortOrder = runPlan.SortOrder,
        Commands = runPlan.Commands
            .OrderBy(item => item.SortOrder)
            .Select(item => new RunPlanCommand
            {
                ProjectId = item.ProjectId,
                CommandId = item.CommandId,
                DelaySeconds = item.DelaySeconds,
                SortOrder = item.SortOrder
            })
            .ToList()
    };

    private void ProcessManagerOnOutputReceived(object? sender, ProcessOutputEventArgs eventArgs)
    {
        _pendingOutput.Enqueue(new PendingProcessOutput(
            eventArgs,
            GetLogGeneration(eventArgs.CommandId)));
        ScheduleOutputFlush();
    }

    private void ClearCommandLog(Guid commandId, bool discardPendingOutput)
    {
        if (discardPendingOutput)
        {
            _logGenerations.AddOrUpdate(commandId, 1, static (_, generation) => unchecked(generation + 1));
        }

        _logs.Remove(commandId);
        if (SelectedCommand?.Command.Id != commandId)
        {
            return;
        }

        _displayedLog = CreateDisplayedLogBuffer();
        ReplaceDisplayedLog(Array.Empty<string>());
    }

    private int GetLogGeneration(Guid commandId) =>
        _logGenerations.GetOrAdd(commandId, 0);

    private void ApplyLogSettings(AppSettings settings)
    {
        LogFontFamily = new FontFamily(settings.LogFontFamily);
        LogFontSize = settings.LogFontSize;
        LogFontStyle = settings.LogFontItalic ? FontStyles.Italic : FontStyles.Normal;
        OnPropertyChanged(nameof(IsLogFontItalic));
    }

    private static void ValidateSettings(AppSettings settings)
    {
        if (settings.Theme is not ("Dark" or "Light"))
        {
            throw new InvalidOperationException("请选择有效的外观模式。");
        }
        if (!ThemeManager.AreThemeColorsValid(settings))
        {
            throw new InvalidOperationException("主题颜色格式无效或前景色与背景色对比度不足。");
        }
        if (settings.CloseBehavior is not ("MinimizeToTray" or "Exit"))
        {
            throw new InvalidOperationException("请选择有效的窗口关闭行为。");
        }
        if (!GlobalHotkey.TryNormalizeGesture(settings.GlobalHotkey, out _, out _))
        {
            throw new InvalidOperationException("请选择有效的全局快捷键。");
        }
        if (settings.LogVisibleLineCount is not (100 or 300 or 500 or 1000))
        {
            throw new InvalidOperationException("请选择有效的日志保留行数。");
        }

        var installedFonts = Fonts.SystemFontFamilies.Select(font => font.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!installedFonts.Contains(settings.UiFontFamily) || !installedFonts.Contains(settings.LogFontFamily))
        {
            throw new InvalidOperationException("所选字体未安装在当前 Windows 系统中。");
        }

        if (!double.IsFinite(settings.UiFontSize) || settings.UiFontSize < 10 || settings.UiFontSize > 24 ||
            !double.IsFinite(settings.LogFontSize) || settings.LogFontSize < 8 || settings.LogFontSize > 40)
        {
            throw new InvalidOperationException("请选择有效的字体大小。");
        }
    }

    private void ProcessManagerOnProcessExited(object? sender, ProcessExitedEventArgs eventArgs)
    {
        var wasExpectedStop = _expectedProcessStops.TryRemove(eventArgs.CommandId, out _);
        var generation = GetLogGeneration(eventArgs.CommandId);
        _pendingOutput.Enqueue(new PendingProcessOutput(
            new ProcessOutputEventArgs(
                eventArgs.CommandId,
                $"[{DateTime.Now:HH:mm:ss}] 进程已退出，代码：{eventArgs.ExitCode}",
                false),
            generation));
        ScheduleOutputFlush();

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        dispatcher.InvokeAsync(() =>
        {
            if (generation != GetLogGeneration(eventArgs.CommandId))
            {
                return;
            }

            if (!wasExpectedStop && eventArgs.ExitCode != 0)
            {
                var exitedRuntime = Commands.FirstOrDefault(item => item.Command.Id == eventArgs.CommandId);
                if (exitedRuntime is not null)
                {
                    AbnormalProcessExited?.Invoke(this, new AbnormalProcessExitEventArgs(
                        eventArgs.CommandId,
                        exitedRuntime.Command.Name,
                        eventArgs.ExitCode));
                }
            }

            var runtime = Commands.FirstOrDefault(item => item.Command.Id == eventArgs.CommandId);
            runtime?.SetRunning(false, $"已退出 ({eventArgs.ExitCode})");
            UpdateRunningCount();
        });
    }

    private void ScheduleOutputFlush()
    {
        if (Interlocked.CompareExchange(ref _outputFlushScheduled, 1, 0) == 0)
        {
            _ = FlushOutputAfterDelayAsync();
        }
    }

    private async Task FlushOutputAfterDelayAsync()
    {
        try
        {
            await Task.Delay(OutputFlushInterval).ConfigureAwait(false);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.HasShutdownStarted)
            {
                await dispatcher.InvokeAsync(FlushPendingOutput, DispatcherPriority.Background);
            }
        }
        catch (TaskCanceledException)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _outputFlushScheduled, 0);
            var dispatcher = Application.Current?.Dispatcher;
            if (!_pendingOutput.IsEmpty && dispatcher is not null && !dispatcher.HasShutdownStarted)
            {
                ScheduleOutputFlush();
            }
        }
    }

    private void FlushPendingOutput()
    {
        var selectedCommandId = SelectedCommand?.Command.Id;
        var outputByCommand = new Dictionary<Guid, StringBuilder>();
        var processedEvents = 0;
        var processedCharacters = 0;
        while (processedEvents < MaximumOutputEventsPerFlush &&
               processedCharacters < MaximumOutputCharactersPerFlush &&
               _pendingOutput.TryDequeue(out var pendingOutput))
        {
            var eventArgs = pendingOutput.EventArgs;
            if (pendingOutput.Generation != GetLogGeneration(eventArgs.CommandId))
            {
                continue;
            }

            if (!outputByCommand.TryGetValue(eventArgs.CommandId, out var outputBatch))
            {
                outputBatch = new StringBuilder();
                outputByCommand[eventArgs.CommandId] = outputBatch;
            }

            var text = eventArgs.IsError
                ? "[错误] " + eventArgs.Text.ReplaceLineEndings(Environment.NewLine + "[错误] ")
                : eventArgs.Text;
            outputBatch.AppendLine(text);
            processedEvents++;
            processedCharacters += text.Length + Environment.NewLine.Length;
        }

        foreach (var (commandId, outputBatch) in outputByCommand)
        {
            if (commandId != selectedCommandId && !_processManager.IsRunning(commandId))
            {
                _logs.Remove(commandId);
                continue;
            }

            AppendLogText(commandId, outputBatch.ToString(), commandId == selectedCommandId);
        }
    }

    private void AppendLog(Guid commandId, string text, bool refreshSelectedLog = true)
    {
        AppendLogText(commandId, text + Environment.NewLine, refreshSelectedLog);
    }

    private void AppendLogText(Guid commandId, string text, bool refreshSelectedLog)
    {
        if (!_logs.TryGetValue(commandId, out var log))
        {
            log = new RollingLogBuffer(MaximumLogCharacters, RetainedLogCharacters);
            _logs[commandId] = log;
        }

        log.Append(text);
        _logFileWriter.Enqueue(commandId, text);
        if (refreshSelectedLog && SelectedCommand?.Command.Id == commandId)
        {
            var change = _displayedLog.Append(text);
            LogDisplayUpdated?.Invoke(this, new LogDisplayUpdateEventArgs(
                null,
                change.RemovedLineCount,
                change.AddedLines));
        }
    }

    private int ActiveLogVisibleLineCount =>
        (_previewLogSettings ?? _data.Settings).LogVisibleLineCount;

    private void RefreshLogText()
    {
        var text = SelectedCommand is not null && _logs.TryGetValue(SelectedCommand.Command.Id, out var log)
            ? log.GetTailLines(ActiveLogVisibleLineCount)
            : string.Empty;
        text = FilterLogText(text);
        _displayedLog = CreateDisplayedLogBuffer();
        _displayedLog.Append(text);
        ReplaceDisplayedLog(_displayedLog.Snapshot());
    }

    private string FilterLogText(string text)
    {
        var search = LogSearchText.Trim();
        if (search.Length == 0)
        {
            return text;
        }

        return string.Join(
            Environment.NewLine,
            text.ReplaceLineEndings("\n")
                .Split('\n')
                .Where(line => search.Length == 0 || line.Contains(search, StringComparison.OrdinalIgnoreCase)));
    }

    private void ReplaceDisplayedLog(IReadOnlyList<string> lines) =>
        LogDisplayUpdated?.Invoke(this, new LogDisplayUpdateEventArgs(lines, 0, Array.Empty<string>()));

    private LogLineBuffer CreateDisplayedLogBuffer() =>
        new(ActiveLogVisibleLineCount);

    private void UpdateRunningCount()
    {
        var groupNamesById = _data.Groups.ToDictionary(item => item.Id, item => item.Name);
        var runtimeSnapshotsByCommandId = _processManager.GetRuntimeSnapshots()
            .ToDictionary(item => item.CommandId);
        var runningCommands = _data.Projects
            .SelectMany(project => project.Commands
                .Where(command => _processManager.IsRunning(command.Id))
                .Select(command =>
                {
                    runtimeSnapshotsByCommandId.TryGetValue(command.Id, out var snapshot);
                    return new RunningCommandSummary(
                        command.Id,
                        project.GroupId.HasValue && groupNamesById.TryGetValue(project.GroupId.Value, out var groupName)
                            ? groupName
                            : string.Empty,
                        project.Name,
                        command.Name,
                        command.CommandText,
                        snapshot?.StartedAt);
                }))
            .OrderBy(item => item.ProjectName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.CommandName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        RunningCommands.Clear();
        foreach (var command in runningCommands)
        {
            RunningCommands.Add(command);
        }

        RunningCount = runningCommands.Count;
        foreach (var item in EnumerateWorkspaceTreeItems())
        {
            if (item.Kind == WorkspaceTreeItemKind.Project && item.Project is not null)
            {
                item.UpdateRuntimeState(item.Project.Commands.Count(command => _processManager.IsRunning(command.Id)));
            }
        }
    }
}

public sealed record RunningCommandSummary(
    Guid CommandId,
    string GroupName,
    string ProjectName,
    string CommandName,
    string CommandText,
    DateTime? StartedAt)
{
    public string ProjectDisplayName => string.IsNullOrWhiteSpace(GroupName)
        ? ProjectName
        : $"{GroupName}-{ProjectName}";

    public string DurationDisplay => StartedAt.HasValue
        ? FormatDuration(DateTime.Now - StartedAt.Value)
        : "未知时长";

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours:0}h {duration.Minutes:00}m";
        }

        return $"{duration.Minutes:0}m {duration.Seconds:00}s";
    }
}

public sealed class RunPlanCommandChoice : ObservableObject
{
    private bool _isSelected;
    private int _delaySeconds;

    public RunPlanCommandChoice(
        Guid projectId,
        Guid commandId,
        string projectDisplayName,
        string commandName,
        bool isSelected,
        int delaySeconds)
    {
        ProjectId = projectId;
        CommandId = commandId;
        ProjectDisplayName = projectDisplayName;
        CommandName = commandName;
        GroupKey = new RunPlanCommandChoiceGroupKey(projectId, projectDisplayName);
        _isSelected = isSelected;
        _delaySeconds = delaySeconds;
    }

    public Guid ProjectId { get; }
    public Guid CommandId { get; }
    public string ProjectDisplayName { get; }
    public string CommandName { get; }
    public RunPlanCommandChoiceGroupKey GroupKey { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public int DelaySeconds
    {
        get => _delaySeconds;
        set => SetProperty(ref _delaySeconds, value);
    }

    public string DisplayName => $"{ProjectDisplayName} / {CommandName}";

    public bool MatchesSearch(string? searchText) =>
        string.IsNullOrWhiteSpace(searchText) ||
        ProjectDisplayName.Contains(searchText.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
        CommandName.Contains(searchText.Trim(), StringComparison.CurrentCultureIgnoreCase);
}

public sealed record RunPlanCommandChoiceGroupKey(Guid ProjectId, string ProjectDisplayName);

public sealed record AbnormalProcessExitEventArgs(Guid CommandId, string CommandName, int ExitCode);

public sealed record LogDisplayUpdateEventArgs(
    IReadOnlyList<string>? ReplacementLines,
    int LinesToRemove,
    IReadOnlyList<string> LinesToAppend);

internal sealed record PendingProcessOutput(ProcessOutputEventArgs EventArgs, int Generation);

public enum GroupFilterKind
{
    All,
    Ungrouped,
    Group
}

public sealed class GroupTreeItem : ObservableObject
{
    private bool _isSelected;

    public GroupTreeItem(string name, GroupFilterKind kind, Guid? groupId)
    {
        Name = name;
        Kind = kind;
        GroupId = groupId;
    }

    public string Name { get; }
    public GroupFilterKind Kind { get; }
    public Guid? GroupId { get; }
    public ObservableCollection<GroupTreeItem> Children { get; } = new();

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public enum WorkspaceTreeItemKind
{
    All,
    Ungrouped,
    Group,
    Project
}

public sealed class WorkspaceTreeItem : ObservableObject
{
    private bool _isSelected;
    private int _projectCount;
    private int _runningCommandCount;

    public WorkspaceTreeItem(
        string name,
        WorkspaceTreeItemKind kind,
        Guid? groupId = null,
        ManagedProject? project = null,
        IReadOnlyList<ManagedProject>? projects = null)
    {
        Name = name;
        Kind = kind;
        GroupId = groupId;
        Project = project;
        _projectCount = projects?.Count ?? 0;
    }

    public string Name { get; }
    public WorkspaceTreeItemKind Kind { get; }
    public Guid? GroupId { get; }
    public ManagedProject? Project { get; }
    public int ProjectCount => _projectCount;
    public int CommandCount => Project?.Commands.Count ?? 0;
    public int RunningCommandCount => _runningCommandCount;
    public string StatusBadge => _runningCommandCount > 0 ? _runningCommandCount.ToString() : string.Empty;
    public string RuntimeBadge => _runningCommandCount > 0 ? $"{_runningCommandCount}/{CommandCount}" : string.Empty;
    public bool HasRunningCommands => _runningCommandCount > 0;
    public ObservableCollection<WorkspaceTreeItem> Children { get; } = new();

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public void SetProjectCount(int count) => SetProperty(ref _projectCount, count);

    public void UpdateRuntimeState(int runningCommandCount)
    {
        if (!SetProperty(ref _runningCommandCount, runningCommandCount))
        {
            return;
        }

        OnPropertyChanged(nameof(StatusBadge));
        OnPropertyChanged(nameof(RuntimeBadge));
        OnPropertyChanged(nameof(HasRunningCommands));
    }
}

public sealed record GroupChoice(Guid? Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class CommandRuntimeViewModel : ObservableObject
{
    private bool _isRunning;
    private string _status;

    public CommandRuntimeViewModel(ProjectCommand command, bool isRunning, string status)
    {
        Command = command;
        _isRunning = isRunning;
        _status = status;
    }

    public ProjectCommand Command { get; }
    public string Name
    {
        get => Command.Name;
        set
        {
            if (Command.Name == value)
            {
                return;
            }

            Command.Name = value;
            OnPropertyChanged();
        }
    }

    public string CommandText
    {
        get => Command.CommandText;
        set
        {
            if (Command.CommandText == value)
            {
                return;
            }

            Command.CommandText = value;
            OnPropertyChanged();
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(CanStop));
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool CanRun => !IsRunning;
    public bool CanStop => IsRunning;

    public void SetRunning(bool isRunning, string status)
    {
        IsRunning = isRunning;
        Status = status;
    }
}
