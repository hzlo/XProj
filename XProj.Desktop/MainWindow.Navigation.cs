using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProjectManager.Wpf;

public partial class MainWindow
{
    // Navigation owns only page visibility and active-rail presentation.
    private async Task ApplyPluginShellAsync(bool enabled)
    {
        PluginTopNavigation.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        NotesNavigationButton.Visibility = enabled && _viewModel.EnableNotes ? Visibility.Visible : Visibility.Collapsed;
        WslNavigationButton.Visibility = enabled && _viewModel.EnableWsl ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled || !_viewModel.EnableWsl)
        {
            await UnloadWslPluginAsync();
        }

        if (!enabled)
        {
            ShowProjectPage();
            return;
        }

        if ((NotesPage.Visibility == Visibility.Visible && !_viewModel.EnableNotes) ||
            (WslPage.Visibility == Visibility.Visible && !_viewModel.EnableWsl))
        {
            ShowPluginManagementPage();
        }
    }

    private void ShowProjectPage() => ShowPage(ProjectPage, ProjectNavigationButton);

    private void ShowNotesPage()
    {
        if (_viewModel.EnablePlugins && _viewModel.EnableNotes)
        {
            ShowPage(NotesPage, NotesNavigationButton);
        }
    }

    private async void ShowWslPage()
    {
        if (_viewModel.EnablePlugins && _viewModel.EnableWsl)
        {
            EnsureWslPluginView();
            ShowPage(WslPage, WslNavigationButton);
            await _wslView!.InitializeAsync();
        }
    }

    private void ShowPluginManagementPage()
    {
        if (_viewModel.EnablePlugins)
        {
            EnableNotesToggle.IsChecked = _viewModel.EnableNotes;
            EnableWslToggle.IsChecked = _viewModel.EnableWsl;
            ShowPage(PluginManagementPage, PluginManagementNavigationButton);
        }
    }

    private void ShowPage(UIElement page, Button activeButton)
    {
        ProjectPage.Visibility = ReferenceEquals(page, ProjectPage) ? Visibility.Visible : Visibility.Collapsed;
        NotesPage.Visibility = ReferenceEquals(page, NotesPage) ? Visibility.Visible : Visibility.Collapsed;
        WslPage.Visibility = ReferenceEquals(page, WslPage) ? Visibility.Visible : Visibility.Collapsed;
        PluginManagementPage.Visibility = ReferenceEquals(page, PluginManagementPage) ? Visibility.Visible : Visibility.Collapsed;

        foreach (var button in new[] { ProjectNavigationButton, NotesNavigationButton, WslNavigationButton, PluginManagementNavigationButton })
        {
            button.ClearValue(BackgroundProperty);
            button.ClearValue(ForegroundProperty);
        }

        activeButton.Background = FindResource("SelectedBrush") as Brush;
        activeButton.Foreground = FindResource("AccentBrush") as Brush;
    }

    private void ProjectNavigation_Click(object sender, RoutedEventArgs e) => ShowProjectPage();
    private void NotesNavigation_Click(object sender, RoutedEventArgs e) => ShowNotesPage();
    private void WslNavigation_Click(object sender, RoutedEventArgs e) => ShowWslPage();
    private void PluginManagementNavigation_Click(object sender, RoutedEventArgs e) => ShowPluginManagementPage();

    private async void EnableNotesToggle_Click(object sender, RoutedEventArgs e)
    {
        var settings = _viewModel.CurrentSettings;
        settings.EnableNotes = EnableNotesToggle.IsChecked == true;
        await ExecuteAsync(async () =>
        {
            await _viewModel.UpdateSettingsAsync(settings);
            await ApplyPluginShellAsync(_viewModel.EnablePlugins);
            ShowPluginManagementPage();
        });
    }

    private async void EnableWslToggle_Click(object sender, RoutedEventArgs e)
    {
        var settings = _viewModel.CurrentSettings;
        settings.EnableWsl = EnableWslToggle.IsChecked == true;
        await ExecuteAsync(async () =>
        {
            await _viewModel.UpdateSettingsAsync(settings);
            await ApplyPluginShellAsync(_viewModel.EnablePlugins);
            ShowPluginManagementPage();
        });
    }
}
