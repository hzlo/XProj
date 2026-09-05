using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Material.Icons;
using Material.Icons.WPF;
using ProjectManager.Wpf.Infrastructure;
using ProjectManager.Wpf.ViewModels;
using ProjectManager.Wpf.Views;
using XProj.Plugin.Abstractions;
using Forms = System.Windows.Forms;
using Models = ProjectManager.Wpf.Models;

namespace ProjectManager.Wpf;

public partial class MainWindow
{
    private readonly SystemLauncher _systemLauncher = new();
    private readonly UpdateChecker _updateChecker = new();
    private readonly UpdateInstaller _updateInstaller = new();
    private MainViewModel _viewModel = null!;
    private readonly List<PluginRegistration> _plugins = new();
    private PluginPackageManager _pluginPackageManager = null!;
    private readonly GlobalHotkeyRegistration _globalHotkey = new();
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
        _pluginPackageManager = new PluginPackageManager(Path.Combine(_viewModel.DataDirectory, "plugins"));
        _pluginPackageManager.ApplyPendingUpdates();
        InitializePlugins();
        DataContext = _viewModel;

        _viewModel.LogDisplayUpdated += ViewModelOnLogDisplayUpdated;
        _viewModel.AbnormalProcessExited += ViewModelOnAbnormalProcessExited;
        _globalHotkey.Pressed += GlobalHotkey_Pressed;
        StateChanged += (_, _) => RefreshDeferredLogDisplay();
        _runningPopoverCloseTimer.Tick += (_, _) => CloseRunningPopoverIfPointerLeft();
        _runningSummaryRefreshTimer.Tick += (_, _) => _viewModel.RefreshRunningSummaries();
        _abnormalExitNotificationTimer.Tick += AbnormalExitNotificationTimer_Tick;
        _runningSummaryRefreshTimer.Start();

        InitializeTray();
    }

    private void InitializePlugins()
    {
        var hostVersion = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var pluginDirectories = new[]
        {
            Path.Combine(_viewModel.DataDirectory, "plugins"),
            Path.Combine(AppContext.BaseDirectory, "Plugins")
        };
        var result = new PluginLoader(pluginDirectories, hostVersion).Load();
        foreach (var loadedPlugin in result.Plugins)
        {
            _plugins.Add(new PluginRegistration { Owner = this, LoadedPlugin = loadedPlugin });
        }

        foreach (var failure in result.Failures)
        {
            _viewModel.SetStatus($"插件加载失败：{Path.GetFileName(failure.PackageDirectory)} - {failure.Message}");
        }

        var pagesGrid = (Grid)ProjectPage.Parent;
        foreach (var registration in _plugins)
        {
            var host = new ContentControl();
            var page = new Border
            {
                Visibility = Visibility.Collapsed,
                Padding = new Thickness(0),
                Child = host
            };
            Grid.SetColumn(page, 1);
            Grid.SetColumnSpan(page, 2);
            page.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            pagesGrid.Children.Add(page);
            registration.Page = page;
            registration.Host = host;

            var navButton = new Button
            {
                Style = (Style)FindResource("RailButton"),
                ToolTip = registration.Plugin.Name,
                Content = new MaterialIcon
                {
                    Kind = registration.Plugin.Icon,
                    Width = 19,
                    Height = 19
                }
            };
            navButton.Click += (_, _) => ShowPluginPage(registration);
            PluginTopNavigation.Children.Insert(PluginTopNavigation.Children.Count - 1, navButton);
            registration.NavButton = navButton;

            registration.ManageToggle = CreatePluginManageCard(registration);
        }

        if (_plugins.Count == 0)
        {
            PluginManagementStatusText.Text = "尚未安装插件。可从插件 Release 安装独立插件包。";
        }
        else
        {
            PluginManagementStatusText.Text = $"已加载 {_plugins.Count} 个插件。插件更新会在重启后生效。";
        }
    }

    private ToggleButton CreatePluginManageCard(PluginRegistration registration)
    {
        if (PluginManagementPage.Child is not StackPanel stackPanel)
        {
            throw new InvalidOperationException("插件管理页结构不符合预期。");
        }

        var card = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Padding = new Thickness(18),
            Margin = new Thickness(0, 10, 0, 0)
        };
        card.SetResourceReference(Border.BackgroundProperty, "SidebarBrush");
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconSurface = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(12)
        };
        iconSurface.SetResourceReference(Border.BackgroundProperty, "SelectedBrush");
        iconSurface.Child = new MaterialIcon
        {
            Kind = registration.Plugin.Icon,
            Width = 21,
            Height = 21,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        ((MaterialIcon)iconSurface.Child).SetResourceReference(MaterialIcon.ForegroundProperty, "AccentBrush");
        layout.Children.Add(iconSurface);

        var description = new StackPanel
        {
            Margin = new Thickness(14, 0, 20, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        description.Children.Add(new TextBlock { Text = registration.Plugin.Name });
        var summary = new TextBlock
        {
            Text = registration.Plugin.Description,
            Margin = new Thickness(0, 4, 0, 0)
        };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        description.Children.Add(summary);
        Grid.SetColumn(description, 1);
        layout.Children.Add(description);

        var toggle = new ToggleButton
        {
            Style = (Style)FindResource("PluginSwitch"),
            IsChecked = registration.IsEnabled(),
            ToolTip = $"启用或关闭{registration.Plugin.Name}",
            Tag = registration
        };
        toggle.Click += PluginManageToggle_Click;
        Grid.SetColumn(toggle, 2);
        layout.Children.Add(toggle);

        card.Child = layout;
        stackPanel.Children.Add(card);
        return toggle;
    }

    private PluginHostContext CreatePluginContext() => new(
        _viewModel.DataDirectory,
        _viewModel.SetStatus,
        (title, message, primaryText) => AppDialog.Confirm(this, title, message, primaryText));

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

    private sealed class PluginRegistration
    {
        public required MainWindow Owner { get; init; }
        public required LoadedPlugin LoadedPlugin { get; init; }

        public IXProjPlugin Plugin => LoadedPlugin.Plugin;
        public PluginManifest Manifest => LoadedPlugin.Manifest;
        public bool IsEnabled() => Owner._viewModel.IsPluginEnabled(Plugin.Id, Manifest.DefaultEnabled);
        public void WriteEnabled(Models.AppSettings settings, bool value) =>
            MainViewModel.SetPluginEnabled(settings, Plugin.Id, value);

        public Button? NavButton { get; set; }
        public ToggleButton? ManageToggle { get; set; }
        public Border? Page { get; set; }
        public ContentControl? Host { get; set; }
        public FrameworkElement? View { get; set; }
    }
}
