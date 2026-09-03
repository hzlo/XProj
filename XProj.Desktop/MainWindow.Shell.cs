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
using XProj.Plugin.DataSync;
using XProj.Plugin.JsonConverter;
using XProj.Plugin.Notes;
using XProj.Plugin.Translator;
using XProj.Plugin.Wsl;
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
        _plugins.Add(new PluginRegistration
        {
            Plugin = new NotesPlugin(),
            IsEnabled = () => _viewModel.EnableNotes,
            WriteEnabled = (settings, value) => settings.EnableNotes = value
        });
        _plugins.Add(new PluginRegistration
        {
            Plugin = new WslPlugin(),
            IsEnabled = () => _viewModel.EnableWsl,
            WriteEnabled = (settings, value) => settings.EnableWsl = value,
            OnShownAsync = static registration => ((WslView)registration.View!).InitializeAsync(),
            OnUnloadAsync = static registration => ((WslView?)registration.View)?.ShutdownAsync() ?? Task.CompletedTask
        });
        _plugins.Add(new PluginRegistration
        {
            Plugin = new JsonConverterPlugin(),
            IsEnabled = () => _viewModel.EnableJsonConverter,
            WriteEnabled = (settings, value) => settings.EnableJsonConverter = value
        });
        _plugins.Add(new PluginRegistration
        {
            Plugin = new TranslatorPlugin(),
            IsEnabled = () => _viewModel.EnableTranslator,
            WriteEnabled = (settings, value) => settings.EnableTranslator = value
        });
        _plugins.Add(new PluginRegistration
        {
            Plugin = new DataSyncPlugin(),
            IsEnabled = () => _viewModel.EnableDataSync,
            WriteEnabled = (settings, value) => settings.EnableDataSync = value
        });

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
        public required IXProjPlugin Plugin { get; init; }
        public required Func<bool> IsEnabled { get; init; }
        public required Action<Models.AppSettings, bool> WriteEnabled { get; init; }
        public Func<PluginRegistration, Task>? OnShownAsync { get; init; }
        public Func<PluginRegistration, Task>? OnUnloadAsync { get; init; }

        public Button? NavButton { get; set; }
        public ToggleButton? ManageToggle { get; set; }
        public Border? Page { get; set; }
        public ContentControl? Host { get; set; }
        public FrameworkElement? View { get; set; }
    }
}
