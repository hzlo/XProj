using System.Windows;
using System.Windows.Threading;
using ProjectManager.Wpf.Infrastructure;
using ProjectManager.Wpf.ViewModels;
using XProj.Plugin.Abstractions;
using XProj.Plugin.Notes;
using Forms = System.Windows.Forms;

namespace ProjectManager.Wpf;

public partial class MainWindow
{
    private readonly SystemLauncher _systemLauncher = new();
    private readonly UpdateChecker _updateChecker = new();
    private readonly UpdateInstaller _updateInstaller = new();
    private MainViewModel _viewModel = null!;
    private readonly IXProjPlugin _notesPlugin = new NotesPlugin();
    private readonly Forms.ContextMenuStrip _trayMenu = new();
    private Forms.NotifyIcon _trayIcon = null!;
    private readonly DispatcherTimer _runningPopoverCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _runningSummaryRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _abnormalExitNotificationTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly List<AbnormalProcessExitEventArgs> _pendingAbnormalExits = new();
    private bool _shutdownCompleted;
    private bool _checkingForUpdates;
    private bool _isExiting;
    private bool _updateRestartConfirmed;
    private bool _logDisplayDirty;
    private Point _dragStartPoint;
    private object? _dragSourceItem;

    private void InitializeShell()
    {
        _viewModel = new MainViewModel(new JsonDataStore(), new ProcessManager(), _systemLauncher);
        PluginContentHost.Content = _notesPlugin.CreateView(new PluginHostContext(_viewModel.DataDirectory, _viewModel.SetStatus));
        DataContext = _viewModel;

        _viewModel.LogDisplayUpdated += ViewModelOnLogDisplayUpdated;
        _viewModel.AbnormalProcessExited += ViewModelOnAbnormalProcessExited;
        StateChanged += (_, _) => RefreshDeferredLogDisplay();
        _runningPopoverCloseTimer.Tick += (_, _) => CloseRunningPopoverIfPointerLeft();
        _runningSummaryRefreshTimer.Tick += (_, _) => _viewModel.RefreshRunningSummaries();
        _abnormalExitNotificationTimer.Tick += AbnormalExitNotificationTimer_Tick;
        _runningSummaryRefreshTimer.Start();

        InitializeTray();
    }

    private void InitializeTray()
    {
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
}
