using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            await ApplyPluginShellAsync(_viewModel.EnablePlugins);
            RegisterGlobalHotkey(_viewModel.CurrentSettings.GlobalHotkey);
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

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var index = key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
            _ => -1
        };
        if (index < 0)
        {
            return;
        }

        var navigationButtons = GetVisibleTopNavigationButtons();
        if (index < navigationButtons.Count)
        {
            navigationButtons[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent, navigationButtons[index]));
            e.Handled = true;
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt || e.Delta == 0)
        {
            return;
        }

        var navigationButtons = GetVisibleTopNavigationButtons();
        if (navigationButtons.Count == 0)
        {
            return;
        }

        var currentIndex = _activeNavigationButton is not null
            ? navigationButtons.IndexOf(_activeNavigationButton)
            : -1;
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        var nextIndex = e.Delta > 0
            ? (currentIndex - 1 + navigationButtons.Count) % navigationButtons.Count
            : (currentIndex + 1) % navigationButtons.Count;
        navigationButtons[nextIndex].RaiseEvent(new RoutedEventArgs(Button.ClickEvent, navigationButtons[nextIndex]));
        e.Handled = true;
    }

    private List<Button> GetVisibleTopNavigationButtons()
    {
        if (ProjectNavigationButton.Parent is not Panel navigationContainer)
        {
            return new List<Button>();
        }

        var buttons = new List<Button>();
        AddNavigationButtons(navigationContainer, buttons);

        return buttons
            .Where(button => button.Visibility == Visibility.Visible && button.IsVisible)
            .ToList();
    }

    private static void AddNavigationButtons(Panel panel, ICollection<Button> buttons)
    {
        foreach (var child in panel.Children)
        {
            switch (child)
            {
                case Button button:
                    buttons.Add(button);
                    break;
                case Panel childPanel:
                    AddNavigationButtons(childPanel, buttons);
                    break;
            }
        }
    }

}
