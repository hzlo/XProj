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
    private readonly SystemLauncher _systemLauncher;
    private readonly UpdateChecker _updateChecker;
    private readonly UpdateInstaller _updateInstaller;
    private readonly MainViewModel _viewModel;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _runningPopoverCloseTimer;
    private readonly DispatcherTimer _runningSummaryRefreshTimer;
    private readonly DispatcherTimer _abnormalExitNotificationTimer;
    private readonly List<AbnormalProcessExitEventArgs> _pendingAbnormalExits = new();
    private bool _shutdownCompleted;
    private bool _checkingForUpdates;
    private bool _isExiting;
    private bool _updateRestartConfirmed;
    private bool _logDisplayDirty;
    private Point _dragStartPoint;
    private object? _dragSourceItem;

    public MainWindow()
    {
        InitializeComponent();
        _systemLauncher = new SystemLauncher();
        _updateChecker = new UpdateChecker();
        _updateInstaller = new UpdateInstaller();
        _viewModel = new MainViewModel(new JsonDataStore(), new ProcessManager(), _systemLauncher);
        _viewModel.LogDisplayUpdated += ViewModelOnLogDisplayUpdated;
        _viewModel.AbnormalProcessExited += ViewModelOnAbnormalProcessExited;
        DataContext = _viewModel;
        StateChanged += (_, _) => RefreshDeferredLogDisplay();

        _runningPopoverCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _runningPopoverCloseTimer.Tick += (_, _) => CloseRunningPopoverIfPointerLeft();
        _runningSummaryRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _runningSummaryRefreshTimer.Tick += (_, _) => _viewModel.RefreshRunningSummaries();
        _runningSummaryRefreshTimer.Start();
        _abnormalExitNotificationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _abnormalExitNotificationTimer.Tick += AbnormalExitNotificationTimer_Tick;

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
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(ShowFromTray);
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
            Hide();
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

}
