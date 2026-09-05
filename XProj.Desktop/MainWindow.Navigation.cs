using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using XProj.Plugin.Abstractions;
using ProjectManager.Wpf.Views;

namespace ProjectManager.Wpf;

public partial class MainWindow
{
    private Button? _activeNavigationButton;

    // Navigation owns only page visibility and active-rail presentation.
    private async Task ApplyPluginShellAsync(bool enabled)
    {
        PluginTopNavigation.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        foreach (var registration in _plugins)
        {
            if (registration.NavButton is not null)
            {
                registration.NavButton.Visibility = enabled && registration.IsEnabled()
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (!enabled || !registration.IsEnabled())
            {
                await UnloadPluginAsync(registration);
            }
        }

        if (!enabled)
        {
            ShowProjectPage();
            return;
        }

        if (_plugins.Any(registration => registration.Page!.Visibility == Visibility.Visible && !registration.IsEnabled()))
        {
            ShowPluginManagementPage();
        }
    }

    private void ShowProjectPage() => ShowPage(ProjectPage, ProjectNavigationButton);

    private async void ShowPluginPage(PluginRegistration registration)
    {
        if (!_viewModel.EnablePlugins || !registration.IsEnabled() || registration.NavButton is null)
        {
            return;
        }

        registration.View ??= registration.Plugin.CreateView(CreatePluginContext());
        registration.Host!.Content = registration.View;
        ShowPage(registration.Page!, registration.NavButton);
        if (registration.Plugin is IXProjPluginLifecycle lifecycle)
        {
            await lifecycle.OnShownAsync(registration.View!);
        }
    }

    private void ShowPluginManagementPage()
    {
        if (_viewModel.EnablePlugins)
        {
            foreach (var registration in _plugins)
            {
                if (registration.ManageToggle is not null)
                {
                    registration.ManageToggle.IsChecked = registration.IsEnabled();
                }
            }

            ShowPage(PluginManagementPage, PluginManagementNavigationButton);
        }
    }

    private void ShowPage(UIElement page, Button activeButton)
    {
        _activeNavigationButton = activeButton;
        ProjectPage.Visibility = ReferenceEquals(page, ProjectPage) ? Visibility.Visible : Visibility.Collapsed;
        PluginManagementPage.Visibility = ReferenceEquals(page, PluginManagementPage) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var registration in _plugins)
        {
            registration.Page!.Visibility = ReferenceEquals(registration.Page, page) ? Visibility.Visible : Visibility.Collapsed;
            registration.NavButton?.ClearValue(BackgroundProperty);
            registration.NavButton?.ClearValue(ForegroundProperty);
        }

        foreach (var button in new[] { ProjectNavigationButton, PluginManagementNavigationButton })
        {
            button.ClearValue(BackgroundProperty);
            button.ClearValue(ForegroundProperty);
        }

        activeButton.Background = FindResource("SelectedBrush") as Brush;
        activeButton.Foreground = FindResource("AccentBrush") as Brush;
    }

    private async Task UnloadPluginAsync(PluginRegistration registration)
    {
        if (registration.View is null)
        {
            return;
        }

        if (registration.Plugin is IXProjPluginLifecycle lifecycle)
        {
            await lifecycle.OnUnloadAsync(registration.View!);
        }

        registration.Host!.Content = null;
        registration.View = null;
    }

    private void ProjectNavigation_Click(object sender, RoutedEventArgs e) => ShowProjectPage();
    private void PluginManagementNavigation_Click(object sender, RoutedEventArgs e) => ShowPluginManagementPage();

    private async void PluginManageToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: PluginRegistration registration })
        {
            return;
        }

        var settings = _viewModel.CurrentSettings;
        registration.WriteEnabled(settings, registration.ManageToggle?.IsChecked == true);
        await ExecuteAsync(async () =>
        {
            await _viewModel.UpdateSettingsAsync(settings);
            await ApplyPluginShellAsync(_viewModel.EnablePlugins);
            ShowPluginManagementPage();
        });
    }

    private async void PluginInstallOrUpdate_Click(object sender, RoutedEventArgs e)
    {
        var pluginId = PluginIdTextBox.Text.Trim();
        if (pluginId.Length == 0 || pluginId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            AppDialog.Show(this, "插件 ID 无效", "请输入插件 Release 中使用的插件 ID，例如 notes 或 json-converter。", AppDialogKind.Warning);
            return;
        }

        await ExecuteAsync(async () =>
        {
            PluginManagementStatusText.Text = $"正在下载插件 {pluginId}...";
            var update = await _pluginPackageManager.DownloadAndStageLatestAsync(pluginId);
            PluginManagementStatusText.Text = $"{pluginId} {update.Version} 已准备完成，重启 XProj 后生效。";
            AppDialog.Show(this, "插件已准备", $"插件 {pluginId} {update.Version} 将在重启 XProj 后完成安装。", AppDialogKind.Information);
        });
    }

    private async void PluginUpdateAll_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync(async () =>
        {
            PluginManagementStatusText.Text = "正在检查已安装插件更新...";
            var updateCount = 0;
            foreach (var registration in _plugins)
            {
                var latest = await _pluginPackageManager.GetLatestAsync(registration.Plugin.Id);
                if (latest is null ||
                    !Version.TryParse(registration.Manifest.Version, out var currentVersion) ||
                    latest.Version <= currentVersion)
                {
                    continue;
                }

                await _pluginPackageManager.DownloadAndStageLatestAsync(registration.Plugin.Id);
                updateCount++;
            }

            PluginManagementStatusText.Text = updateCount == 0
                ? "已安装插件均为最新版本。"
                : $"已准备 {updateCount} 个插件更新，重启 XProj 后生效。";
        });
    }
}
