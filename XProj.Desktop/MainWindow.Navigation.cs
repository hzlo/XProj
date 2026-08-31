using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProjectManager.Wpf;

public partial class MainWindow
{
    // Navigation owns only page visibility and active-rail presentation.
    private void ApplyPluginShell(bool enabled)
    {
        PluginTopNavigation.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        NotesNavigationButton.Visibility = enabled && _viewModel.EnableNotes ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled)
        {
            ShowProjectPage();
            return;
        }

        if (NotesPage.Visibility == Visibility.Visible && !_viewModel.EnableNotes)
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

    private void ShowPluginManagementPage()
    {
        if (_viewModel.EnablePlugins)
        {
            EnableNotesToggle.IsChecked = _viewModel.EnableNotes;
            ShowPage(PluginManagementPage, PluginManagementNavigationButton);
        }
    }

    private void ShowPage(UIElement page, Button activeButton)
    {
        ProjectPage.Visibility = ReferenceEquals(page, ProjectPage) ? Visibility.Visible : Visibility.Collapsed;
        NotesPage.Visibility = ReferenceEquals(page, NotesPage) ? Visibility.Visible : Visibility.Collapsed;
        PluginManagementPage.Visibility = ReferenceEquals(page, PluginManagementPage) ? Visibility.Visible : Visibility.Collapsed;

        foreach (var button in new[] { ProjectNavigationButton, NotesNavigationButton, PluginManagementNavigationButton })
        {
            button.ClearValue(BackgroundProperty);
            button.ClearValue(ForegroundProperty);
        }

        activeButton.Background = FindResource("SelectedBrush") as Brush;
        activeButton.Foreground = FindResource("AccentBrush") as Brush;
    }

    private void ProjectNavigation_Click(object sender, RoutedEventArgs e) => ShowProjectPage();
    private void NotesNavigation_Click(object sender, RoutedEventArgs e) => ShowNotesPage();
    private void PluginManagementNavigation_Click(object sender, RoutedEventArgs e) => ShowPluginManagementPage();

    private async void EnableNotesToggle_Click(object sender, RoutedEventArgs e)
    {
        var settings = _viewModel.CurrentSettings;
        settings.EnableNotes = EnableNotesToggle.IsChecked == true;
        await ExecuteAsync(async () =>
        {
            await _viewModel.UpdateSettingsAsync(settings);
            ApplyPluginShell(_viewModel.EnablePlugins);
            ShowPluginManagementPage();
        });
    }
}
