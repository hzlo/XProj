using System.ComponentModel;
using System.Windows;
using ProjectManager.Wpf.Infrastructure;

namespace ProjectManager.Wpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        InitializeShell();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
            ThemeManager.Apply(_viewModel.CurrentSettings);
            ApplyPluginShell(_viewModel.EnablePlugins);
            _ = CheckForUpdatesAsync(this, showUpToDateMessage: false);
        }
        catch (Exception exception)
        {
            ShowError("加载项目数据失败", exception);
    }
}

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownCompleted || _isExiting)
        {
            return;
        }

        CloseRunningPopover();
        e.Cancel = true;
        if (_viewModel.CurrentSettings.CloseBehavior == "Exit")
        {
            await ExitApplicationAsync();
        }
        else
        {
            Hide();
        }
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            CloseRunningPopover();
        }

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

    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            CloseRunningPopover();
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        CloseRunningPopover();
    }

}
