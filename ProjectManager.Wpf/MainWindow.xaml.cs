using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ProjectManager.Wpf.Infrastructure;
using ProjectManager.Wpf.Models;
using ProjectManager.Wpf.ViewModels;
using ProjectManager.Wpf.Views;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ProjectManager.Wpf;

public partial class MainWindow : Window
{
    private readonly SystemLauncher _systemLauncher;
    private readonly UpdateChecker _updateChecker;
    private readonly UpdateInstaller _updateInstaller;
    private readonly MainViewModel _viewModel;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _runningPopoverCloseTimer;
    private bool _shutdownCompleted;
    private bool _checkingForUpdates;
    private bool _isExiting;
    private bool _updateRestartConfirmed;
    private bool _logDisplayDirty;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        _systemLauncher = new SystemLauncher();
        _updateChecker = new UpdateChecker();
        _updateInstaller = new UpdateInstaller();
        _viewModel = new MainViewModel(new JsonDataStore(), new ProcessManager(), _systemLauncher);
        _viewModel.LogDisplayUpdated += ViewModelOnLogDisplayUpdated;
        DataContext = _viewModel;
        StateChanged += (_, _) => RefreshDeferredLogDisplay();

        _runningPopoverCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _runningPopoverCloseTimer.Tick += (_, _) => CloseRunningPopoverIfPointerLeft();

        _trayMenu = new Forms.ContextMenuStrip();
        var showItem = new Forms.ToolStripMenuItem("打开 XProj");
        showItem.Click += (_, _) => Dispatcher.Invoke(ShowFromTray);
        var checkUpdatesItem = new Forms.ToolStripMenuItem("检查更新");
        checkUpdatesItem.Click += TrayCheckUpdates_Click;
        var exitItem = new Forms.ToolStripMenuItem("完全退出");
        exitItem.Click += TrayExit_Click;
        _trayMenu.Items.Add(showItem);
        _trayMenu.Items.Add(checkUpdatesItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);

        _trayIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = _trayMenu,
            Icon = LoadTrayIcon(),
            Text = "XProj 项目管理器",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
            ThemeManager.Apply(_viewModel.CurrentSettings);
            _ = CheckForUpdatesAsync(this, showUpToDateMessage: false);
        }
        catch (Exception exception)
        {
            ShowError("加载项目数据失败", exception);
        }
    }

    private void RunningStatus_MouseEnter(object sender, MouseEventArgs e) => ShowRunningPopover();

    private void RunningStatus_MouseLeave(object sender, MouseEventArgs e) => ScheduleRunningPopoverClose();

    private void RunningPopover_MouseEnter(object sender, MouseEventArgs e) => _runningPopoverCloseTimer.Stop();

    private void RunningPopover_MouseLeave(object sender, MouseEventArgs e) => ScheduleRunningPopoverClose();

    private void RunningStatus_Click(object sender, RoutedEventArgs e)
    {
        if (RunningStatusButton.IsChecked == true)
        {
            ShowRunningPopover();
        }
        else
        {
            ScheduleRunningPopoverClose();
        }
    }

    private void RunningStatusPopup_Closed(object? sender, EventArgs e)
    {
        _runningPopoverCloseTimer.Stop();
        RunningStatusButton.IsChecked = false;
    }

    private void ShowRunningPopover()
    {
        _runningPopoverCloseTimer.Stop();
        RunningStatusPopup.IsOpen = true;
    }

    private void ScheduleRunningPopoverClose()
    {
        if (RunningStatusButton.IsChecked == true)
        {
            return;
        }

        _runningPopoverCloseTimer.Stop();
        _runningPopoverCloseTimer.Start();
    }

    private void CloseRunningPopoverIfPointerLeft()
    {
        _runningPopoverCloseTimer.Stop();
        if (RunningStatusButton.IsChecked != true &&
            !RunningStatusButton.IsMouseOver &&
            !RunningPopoverSurface.IsMouseOver)
        {
            RunningStatusPopup.IsOpen = false;
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownCompleted || _isExiting)
        {
            return;
        }

        e.Cancel = true;
        if (_viewModel.CurrentSettings.CloseBehavior == "Exit")
        {
            await ExitApplicationAsync();
        }
        else
        {
            HideToTray();
        }
    }

    private void GroupTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is GroupTreeItem group)
        {
            _viewModel.SelectedGroup = group;
        }
    }

    private async void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        var defaultParentId = _viewModel.SelectedGroup?.Kind == GroupFilterKind.Group
            ? _viewModel.SelectedGroup.GroupId
            : null;
        await ShowAddGroupDialogAsync(defaultParentId);
    }

    private async void AddSiblingGroup_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedGroup;
        if (selected?.Kind != GroupFilterKind.Group || !selected.GroupId.HasValue)
        {
            return;
        }

        await ShowAddGroupDialogAsync(FindParentGroupId(selected.GroupId.Value));
    }

    private async Task ShowAddGroupDialogAsync(Guid? parentId)
    {
        var dialog = new GroupDialog(_viewModel.GetGroupChoices(), "新建分组", initialParentId: parentId)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ExecuteAsync(() => _viewModel.AddGroupAsync(dialog.GroupName, dialog.ParentGroupId));
    }

    private async void EditGroup_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedGroup;
        if (selected?.Kind != GroupFilterKind.Group || !selected.GroupId.HasValue)
        {
            return;
        }

        var currentChoice = _viewModel.GetGroupChoices().FirstOrDefault(item => item.Id == selected.GroupId);
        var currentParentId = FindParentGroupId(selected.GroupId.Value);
        var dialog = new GroupDialog(
            _viewModel.GetGroupChoices(selected.GroupId),
            "编辑分组",
            currentChoice?.DisplayName.TrimStart('　') ?? selected.Name,
            currentParentId)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ExecuteAsync(() => _viewModel.UpdateGroupAsync(selected.GroupId.Value, dialog.GroupName, dialog.ParentGroupId));
    }

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedGroup;
        if (selected?.Kind != GroupFilterKind.Group || !selected.GroupId.HasValue)
        {
            return;
        }

        if (AppDialog.Confirm(
                this,
                "删除分组",
                $"确定删除分组“{selected.Name}”吗？\n\n其子分组和项目会移动到上一级，不会被删除。",
                "删除分组"))
        {
            await ExecuteAsync(() => _viewModel.DeleteGroupAsync(selected.GroupId.Value));
        }
    }

    private async void AddProject_Click(object sender, RoutedEventArgs e)
    {
        var defaultGroupId = _viewModel.SelectedGroup?.Kind == GroupFilterKind.Group
            ? _viewModel.SelectedGroup.GroupId
            : null;
        var dialog = new ProjectDialog(_viewModel.GetGroupChoices(), "添加项目", defaultGroupId: defaultGroupId)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            await ExecuteAsync(() => _viewModel.AddProjectAsync(dialog.Result));
        }
    }

    private async void EditProject_Click(object sender, RoutedEventArgs e)
    {
        var project = _viewModel.SelectedProject;
        if (project is null)
        {
            return;
        }

        var dialog = new ProjectDialog(_viewModel.GetGroupChoices(), "编辑项目", project)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            await ExecuteAsync(() => _viewModel.UpdateProjectAsync(dialog.Result));
        }
    }

    private async void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        var project = _viewModel.SelectedProject;
        if (project is null)
        {
            return;
        }

        if (AppDialog.Confirm(
                this,
                "删除项目",
                $"确定删除项目“{project.Name}”吗？\n\n运行中的命令会先停止，项目目录不会被删除。",
                "删除项目"))
        {
            await ExecuteAsync(() => _viewModel.DeleteProjectAsync(project.Id));
        }
    }

    private async void CommandButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CommandRuntimeViewModel command)
        {
            _viewModel.SelectedCommand = command;
            if (!command.IsRunning)
            {
                await ExecuteAsync(() => _viewModel.RunCommandAsync(command));
            }
        }
    }

    private async void RunCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedCommand is { IsRunning: false } command)
        {
            await ExecuteAsync(() => _viewModel.RunCommandAsync(command));
        }
    }

    private async void StopCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedCommand is { IsRunning: true } command)
        {
            await ExecuteAsync(() => _viewModel.StopCommandAsync(command));
        }
    }

    private async void RestartCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedCommand is { } command)
        {
            await ExecuteAsync(() => _viewModel.RestartCommandAsync(command));
        }
    }

    private async void EditCommand_Click(object sender, RoutedEventArgs e)
    {
        var command = _viewModel.SelectedCommand;
        if (command is null)
        {
            return;
        }

        if (command.IsRunning)
        {
            if (!AppDialog.Confirm(
                    this,
                    "编辑运行中的命令",
                    $"编辑“{command.Name}”前需要先停止当前进程。是否继续？",
                    "停止并编辑"))
            {
                return;
            }

            await ExecuteAsync(() => _viewModel.StopCommandAsync(command));
            if (command.IsRunning)
            {
                return;
            }
        }

        var dialog = new CommandDialog(command.Command) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await ExecuteAsync(() => _viewModel.UpdateCommandAsync(
                command.Command.Id,
                dialog.CommandName,
                dialog.CommandText));
        }
    }

    private async void DeleteCommand_Click(object sender, RoutedEventArgs e)
    {
        var command = _viewModel.SelectedCommand;
        if (command is null)
        {
            return;
        }

        var message = command.IsRunning
            ? $"命令“{command.Name}”正在运行，删除前会先停止进程。是否继续？"
            : $"确定删除命令“{command.Name}”吗？";
        if (AppDialog.Confirm(this, "删除命令", message, "删除命令"))
        {
            await ExecuteAsync(() => _viewModel.DeleteCommandAsync(command.Command.Id));
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _viewModel.ClearSelectedLog();

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(
            _viewModel.CurrentSettings,
            _viewModel.ExportConfigurationAsync,
            async filePath =>
            {
                var settings = await _viewModel.ImportConfigurationAsync(filePath);
                ThemeManager.Apply(settings);
                return settings;
            },
            UpdateChecker.CurrentVersionDisplay,
            owner => CheckForUpdatesAsync(owner, showUpToDateMessage: true))
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            await ExecuteAsync(async () =>
            {
                await _viewModel.UpdateSettingsAsync(dialog.Result);
                ThemeManager.Apply(dialog.Result);
            });
        }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void GroupContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu contextMenu &&
            contextMenu.PlacementTarget is FrameworkElement target &&
            target.DataContext is GroupTreeItem group)
        {
            _viewModel.SelectedGroup = group;
        }
    }

    private void ProjectContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu contextMenu &&
            contextMenu.PlacementTarget is FrameworkElement target &&
            target.DataContext is ManagedProject project)
        {
            _viewModel.SelectedProject = project;
        }
    }

    private void CommandContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu contextMenu &&
            contextMenu.PlacementTarget is FrameworkElement target &&
            target.DataContext is CommandRuntimeViewModel command)
        {
            _viewModel.SelectedCommand = command;
        }
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (MaximizeButton is not null)
        {
            MaximizeButton.Content = new Material.Icons.WPF.MaterialIcon
            {
                Kind = WindowState == WindowState.Maximized
                    ? Material.Icons.MaterialIconKind.WindowRestore
                    : Material.Icons.MaterialIconKind.WindowMaximize,
                Width = 14,
                Height = 14
            };
            MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Execute(() => _viewModel.OpenSelectedProjectFolder());

    private void OpenTerminal_Click(object sender, RoutedEventArgs e) =>
        Execute(() => _viewModel.OpenSelectedProjectTerminal());

    private void OpenEditor_Click(object sender, RoutedEventArgs e) =>
        Execute(() => _viewModel.OpenSelectedProjectEditor());

    private void ViewModelOnLogDisplayUpdated(object? sender, LogDisplayUpdateEventArgs eventArgs)
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            _logDisplayDirty = true;
            return;
        }

        if (eventArgs.ReplacementText is not null)
        {
            LogTextBox.Text = eventArgs.ReplacementText;
        }
        else
        {
            if (eventArgs.CharactersToRemove > 0)
            {
                LogTextBox.Select(0, Math.Min(eventArgs.CharactersToRemove, LogTextBox.Text.Length));
                LogTextBox.SelectedText = string.Empty;
            }

            LogTextBox.AppendText(eventArgs.TextToAppend);
        }

        LogTextBox.ScrollToEnd();
    }

    private void RefreshDeferredLogDisplay()
    {
        if (!_logDisplayDirty || !IsVisible || WindowState == WindowState.Minimized)
        {
            return;
        }

        _logDisplayDirty = false;
        _viewModel.RefreshLogDisplay();
    }

    private Guid? FindParentGroupId(Guid groupId)
    {
        GroupTreeItem? FindParent(IEnumerable<GroupTreeItem> items, Guid targetId)
        {
            foreach (var item in items)
            {
                if (item.Children.Any(child => child.GroupId == targetId))
                {
                    return item;
                }

                var nested = FindParent(item.Children, targetId);
                if (nested is not null)
                {
                    return nested;
                }
            }

            return null;
        }

        return FindParent(_viewModel.GroupItems, groupId)?.GroupId;
    }

    private void HideToTray()
    {
        Hide();
        if (_trayHintShown)
        {
            return;
        }

        _trayHintShown = true;
        _trayIcon.BalloonTipTitle = "XProj 仍在运行";
        _trayIcon.BalloonTipText = "项目进程会继续运行。双击托盘图标可恢复窗口。";
        _trayIcon.ShowBalloonTip(2500);
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        RefreshDeferredLogDisplay();
        Activate();
    }

    private async void TrayExit_Click(object? sender, EventArgs e)
    {
        await ExitApplicationAsync();
    }

    private void TrayCheckUpdates_Click(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            ShowFromTray();
            _ = CheckForUpdatesAsync(this, showUpToDateMessage: true);
        });
    }

    private async Task CheckForUpdatesAsync(Window owner, bool showUpToDateMessage)
    {
        if (_checkingForUpdates)
        {
            if (showUpToDateMessage)
            {
                AppDialog.Show(ResolveDialogOwner(owner), "检查更新", "正在检查更新，请稍候。");
            }
            return;
        }

        _checkingForUpdates = true;
        try
        {
            var result = await _updateChecker.CheckAsync(forceRefresh: showUpToDateMessage);
            if (result.IsUpdateAvailable)
            {
                if (!IsVisible && !showUpToDateMessage)
                {
                    _trayIcon.BalloonTipTitle = "XProj 有新版本";
                    _trayIcon.BalloonTipText = $"v{result.LatestVersion} 已发布，可从托盘菜单检查更新。";
                    _trayIcon.ShowBalloonTip(4000);
                    return;
                }

                var dialogOwner = ResolveDialogOwner(owner);
                if (AppDialog.Confirm(
                        dialogOwner,
                        "发现新版本",
                        $"当前版本：v{result.CurrentVersion}\n最新版本：v{result.LatestVersion}\n\n是否立即下载并安装更新？",
                        "下载更新",
                        AppDialogKind.Information))
                {
                    await DownloadAndInstallUpdateAsync(dialogOwner, result);
                }
            }
            else if (showUpToDateMessage)
            {
                AppDialog.Show(
                    ResolveDialogOwner(owner),
                    "已是最新版本",
                    $"当前版本 v{result.CurrentVersion} 已是最新版本。");
            }
        }
        catch (Exception exception)
        {
            if (showUpToDateMessage)
            {
                var message = exception is TaskCanceledException
                    ? "连接 GitHub 超时，请稍后重试。"
                    : $"无法检查更新：{exception.Message}";
                AppDialog.Show(ResolveDialogOwner(owner), "检查更新失败", message, AppDialogKind.Error);
            }
        }
        finally
        {
            _checkingForUpdates = false;
        }
    }

    private static Window ResolveDialogOwner(Window preferredOwner) =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) ?? preferredOwner;

    private async Task DownloadAndInstallUpdateAsync(Window owner, UpdateCheckResult result)
    {
        string? packagePath = null;
        try
        {
            var progress = new Progress<int>(percent =>
                _viewModel.SetStatus($"正在下载更新 v{result.LatestVersion}… {percent}%"));
            _viewModel.SetStatus($"正在下载更新 v{result.LatestVersion}…");
            packagePath = await _updateInstaller.DownloadAsync(result, progress);
            _viewModel.SetStatus($"更新 v{result.LatestVersion} 下载完成");

            if (!AppDialog.Confirm(
                    owner,
                    "下载完成",
                    $"新版本 v{result.LatestVersion} 已下载完成。\n\n重启应用后自动完成安装，是否立即重启？",
                    "重启并更新",
                    AppDialogKind.Information))
            {
                _updateInstaller.DiscardPackage(packagePath);
                _viewModel.SetStatus($"已取消安装 v{result.LatestVersion}，可稍后重新检查更新");
                return;
            }

            if (_viewModel.RunningCount > 0 && !AppDialog.Confirm(
                    owner,
                    "重启并更新",
                    $"当前有 {_viewModel.RunningCount} 个命令正在运行，重启前将自动停止它们。是否继续？",
                    "停止并重启"))
            {
                _updateInstaller.DiscardPackage(packagePath);
                _viewModel.SetStatus($"已取消安装 v{result.LatestVersion}，可稍后重新检查更新");
                return;
            }

            _updateRestartConfirmed = true;
            _updateInstaller.ScheduleApplyAndRelaunch(packagePath);
            _viewModel.SetStatus("正在重启以完成更新…");
            await ExitApplicationAsync();
        }
        catch (Exception exception)
        {
            _updateRestartConfirmed = false;
            if (packagePath is not null)
            {
                _updateInstaller.DiscardPackage(packagePath);
            }

            var message = exception is TaskCanceledException
                ? "下载更新超时，请检查网络后重试。"
                : $"无法完成更新：{exception.Message}";
            AppDialog.Show(owner, "更新失败", message, AppDialogKind.Error);
            _viewModel.SetStatus("更新失败");
        }
    }

    private async Task ExitApplicationAsync()
    {
        if (_isExiting)
        {
            return;
        }

        if (!_updateRestartConfirmed && _viewModel.RunningCount > 0)
        {
            ShowFromTray();
            if (!AppDialog.Confirm(
                    this,
                    "完全退出 XProj",
                    $"当前有 {_viewModel.RunningCount} 个命令正在运行。完全退出会停止所有进程，是否继续？",
                    "停止并退出"))
            {
                return;
            }
        }

        _isExiting = true;
        try
        {
            await _viewModel.ShutdownAsync();
            _shutdownCompleted = true;
            _trayIcon.Visible = false;
            var trayImage = _trayIcon.Icon;
            _trayIcon.Dispose();
            trayImage?.Dispose();
            _trayMenu.Dispose();
            Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            _isExiting = false;
            _updateRestartConfirmed = false;
            ShowFromTray();
            ShowError("退出前清理进程失败", exception);
        }
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico"));
        if (resource is null)
        {
            return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
        }

        using var stream = resource.Stream;
        using var icon = new Drawing.Icon(stream);
        return (Drawing.Icon)icon.Clone();
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            ShowError("操作失败", exception);
        }
    }

    private void Execute(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            ShowError("操作失败", exception);
        }
    }

    private void ShowError(string title, Exception exception) =>
        AppDialog.Show(this, title, exception.Message, AppDialogKind.Error);
}
