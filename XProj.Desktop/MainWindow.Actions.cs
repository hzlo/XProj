using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
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
    private void GroupTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is WorkspaceTreeItem item)
        {
            _viewModel.SelectWorkspaceItem(item);
        }
    }

    private void WorkspaceItemHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is DependencyObject surface &&
            FindAncestor<TreeViewItem>(surface) is { } item &&
            item.HasItems)
        {
            var shouldExpand = !item.IsExpanded;
            if (shouldExpand && ItemsControl.ItemsControlFromItemContainer(item) is { } parent)
            {
                foreach (var siblingData in parent.Items)
                {
                    if (parent.ItemContainerGenerator.ContainerFromItem(siblingData) is TreeViewItem sibling &&
                        !ReferenceEquals(sibling, item))
                    {
                        sibling.IsExpanded = false;
                    }
                }
            }

            item.IsExpanded = shouldExpand;
        }
    }

    private void WorkspaceContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget: FrameworkElement target } contextMenu ||
            target.DataContext is not WorkspaceTreeItem item)
        {
            return;
        }

        _viewModel.SelectWorkspaceItem(item);
        contextMenu.Items.OfType<MenuItem>().FirstOrDefault(menuItem => menuItem.Header?.ToString() == "编辑分组")!.Visibility =
            item.Kind == WorkspaceTreeItemKind.Group ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void DeleteWorkspaceItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WorkspaceTreeItem item)
        {
            return;
        }

        if (item.Kind == WorkspaceTreeItemKind.Project && item.Project is not null)
        {
            await DeleteProjectAsync(item.Project);
        }
        else if (item.Kind == WorkspaceTreeItemKind.Group && item.GroupId.HasValue)
        {
            await DeleteGroupAsync(item.GroupId.Value);
        }
    }

    private async Task DeleteProjectAsync(ManagedProject project)
    {
        _viewModel.SelectedProject = project;
        if (AppDialog.Confirm(this, "删除项目", $"确定删除项目“{project.Name}”吗？\n\n运行中的命令会先停止，项目目录不会被删除。", "删除项目"))
        {
            await ExecuteAsync(() => _viewModel.DeleteProjectAsync(project.Id));
        }
    }

    private async Task DeleteGroupAsync(Guid groupId)
    {
        var group = _viewModel.GroupItems.SelectMany(FlattenGroups).FirstOrDefault(item => item.GroupId == groupId);
        if (group is not null && AppDialog.Confirm(this, "删除分组", $"确定删除分组“{group.Name}”吗？\n\n其子分组和项目会移动到上一级，不会被删除。", "删除分组"))
        {
            await ExecuteAsync(() => _viewModel.DeleteGroupAsync(groupId));
        }
    }

    private static IEnumerable<GroupTreeItem> FlattenGroups(GroupTreeItem item)
    {
        yield return item;
        foreach (var child in item.Children.SelectMany(FlattenGroups))
        {
            yield return child;
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

    private async void StopAllCommands_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasRunningCommands)
        {
            return;
        }

        await ExecuteAsync(_viewModel.StopAllCommandsAsync);
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
                dialog.CommandText,
                dialog.Shell,
                dialog.EnvironmentVariables));
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
                RegisterGlobalHotkey(settings.GlobalHotkey);
                return settings;
            },
            settings =>
            {
                ThemeManager.Apply(settings);
                _viewModel.PreviewLogSettings(settings);
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
                await ApplyPluginShellAsync(dialog.Result.EnablePlugins);
                RegisterGlobalHotkey(dialog.Result.GlobalHotkey);
            });
            return;
        }

        var currentSettings = _viewModel.CurrentSettings;
        ThemeManager.Apply(currentSettings);
        _viewModel.RestoreLogSettingsPreview();
    }

    private async void RunPlanSidebar_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is RunPlan runPlan)
        {
            await ExecuteAsync(() => _viewModel.RunPlanAsync(runPlan.Id));
        }
    }

    private async void RunPlans_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RunPlansDialog(
            _viewModel.GetRunPlansSnapshot(),
            _viewModel.GetRunPlanCommandChoices)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            await _viewModel.ReplaceRunPlansAsync(dialog.Result);
            if (dialog.RunPlanIdToStart.HasValue)
            {
                await _viewModel.RunPlanAsync(dialog.RunPlanIdToStart.Value);
            }
        });
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

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Execute(() => _viewModel.OpenSelectedProjectFolder());

    private void OpenTerminal_Click(object sender, RoutedEventArgs e) =>
        Execute(() => _viewModel.OpenSelectedProjectTerminal());

    private void OpenEditor_Click(object sender, RoutedEventArgs e) =>
        Execute(() => _viewModel.OpenSelectedProjectEditor());

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
    private async void SendCommandInput_Click(object sender, RoutedEventArgs e) => await SendCommandInputAsync();

    private async void CommandInputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SendCommandInputAsync();
        }
    }

    private async void CommandCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if ((sender as FrameworkElement)?.DataContext is not CommandRuntimeViewModel command)
        {
            return;
        }

        _viewModel.SelectedCommand = command;
        if (!command.IsRunning)
        {
            await ExecuteAsync(() => _viewModel.RunCommandAsync(command));
        }
    }

    private async void CommandCardAction_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not CommandRuntimeViewModel command)
        {
            return;
        }

        _viewModel.SelectedCommand = command;
        await ExecuteAsync(() => command.IsRunning
            ? _viewModel.StopCommandAsync(command)
            : _viewModel.RunCommandAsync(command));
    }

    private async void StopRunningCommand_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is RunningCommandSummary command)
        {
            await ExecuteAsync(() => _viewModel.StopCommandAsync(command.CommandId));
        }
    }

    private async Task SendCommandInputAsync()
    {
        var text = CommandInputTextBox.Text;
        if (text.Length == 0)
        {
            return;
        }

        await ExecuteAsync(() => _viewModel.SendInputAsync(text));
        CommandInputTextBox.Clear();
    }
}
