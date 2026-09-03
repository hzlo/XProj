using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using XProj.Plugin.Abstractions;

namespace ProjectManager.Wpf;

public partial class MainWindow
{
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
        if (registration.OnShownAsync is not null)
        {
            await registration.OnShownAsync(registration);
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

        if (registration.OnUnloadAsync is not null)
        {
            await registration.OnUnloadAsync(registration);
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
}
