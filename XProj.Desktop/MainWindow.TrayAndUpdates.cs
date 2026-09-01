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
    private void ViewModelOnAbnormalProcessExited(object? sender, AbnormalProcessExitEventArgs eventArgs)
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            return;
        }

        _pendingAbnormalExits.Add(eventArgs);
        _abnormalExitNotificationTimer.Stop();
        _abnormalExitNotificationTimer.Start();
    }

    private void AbnormalExitNotificationTimer_Tick(object? sender, EventArgs e)
    {
        _abnormalExitNotificationTimer.Stop();
        if (_pendingAbnormalExits.Count == 0)
        {
            return;
        }

        var exits = _pendingAbnormalExits.GroupBy(item => item.CommandId).Select(group => group.First()).ToArray();
        _pendingAbnormalExits.Clear();
        _trayIcon.BalloonTipTitle = "XProj 命令异常退出";
        _trayIcon.BalloonTipText = exits.Length == 1 ? $"{exits[0].CommandName} 已异常退出（代码 {exits[0].ExitCode}）。" : $"有 {exits.Length} 个命令异常退出：{string.Join("、", exits.Select(item => item.CommandName))}";
        _trayIcon.ShowBalloonTip(5000);
    }

    internal void ShowFromExternalActivation()
    {
        if (_isExiting || _shutdownCompleted || !IsLoaded)
        {
            return;
        }

        ShowFromTray();
    }

    private void ShowFromTray()
    {
        if (_isExiting || _shutdownCompleted || !IsLoaded)
        {
            return;
        }

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
            await UnloadWslPluginAsync();
            await _viewModel.ShutdownAsync();
            _globalHotkey.Dispose();
            _trayIcon.Visible = false;
            var trayImage = _trayIcon.Icon;
            _trayIcon.Dispose();
            trayImage?.Dispose();
            _trayMenu.Dispose();
            _shutdownCompleted = true;
            if (Application.Current is App app)
            {
                app.ShutdownApplication();
            }
            else
            {
                Application.Current.Shutdown();
            }
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

}
