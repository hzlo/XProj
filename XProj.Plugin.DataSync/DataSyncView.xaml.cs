using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XProj.Plugin.Abstractions;

namespace XProj.Plugin.DataSync;

public partial class DataSyncView : UserControl
{
    private readonly PluginHostContext _context;
    private readonly SyncEngine _engine = new();
    private readonly DispatcherTimer _autoSyncTimer;
    private DataSyncSettings _settings = new();
    private CancellationTokenSource? _operationCancellation;
    private bool _loading = true;

    public DataSyncView(PluginHostContext context)
    {
        _context = context;
        InitializeComponent();
        DataContext = this;
        ConflictBox.ItemsSource = new[] { "本地优先", "远程优先", "按修改时间", "冲突时跳过" };
        _autoSyncTimer = new DispatcherTimer();
        _autoSyncTimer.Tick += AutoSyncTimer_Tick;
        Loaded += async (_, _) => await LoadSettingsAsync();
        Unloaded += async (_, _) =>
        {
            _operationCancellation?.Cancel();
            await SaveSettingsAsync();
        };
    }

    public ObservableCollection<string> LogLines { get; } = new();

    private async Task LoadSettingsAsync()
    {
        _settings = await DataSyncSettings.LoadAsync(_context.DataDirectory);
        _loading = true;
        EndpointBox.Text = _settings.Endpoint;
        UsernameBox.Text = _settings.Username;
        PasswordBox.Password = _settings.Password;
        RemoteDirectoryBox.Text = _settings.RemoteDirectory;
        AutoSyncCheckBox.IsChecked = _settings.AutoSync;
        IntervalBox.Text = _settings.IntervalMinutes.ToString();
        ConflictBox.SelectedIndex = (int)_settings.ConflictStrategy;
        _loading = false;
        UpdateTimer();
    }

    private void CaptureSettings()
    {
        _settings.Endpoint = EndpointBox.Text.Trim();
        _settings.Username = UsernameBox.Text.Trim();
        _settings.Password = PasswordBox.Password;
        _settings.RemoteDirectory = RemoteDirectoryBox.Text.Trim();
        _settings.AutoSync = AutoSyncCheckBox.IsChecked == true;
        _settings.IntervalMinutes = int.TryParse(IntervalBox.Text, out var interval) ? Math.Clamp(interval, 1, 1440) : 30;
        _settings.ConflictStrategy = (SyncConflictStrategy)Math.Clamp(ConflictBox.SelectedIndex, 0, 3);
    }

    private async Task SaveSettingsAsync()
    {
        CaptureSettings();
        await _settings.SaveAsync(_context.DataDirectory);
    }

    private async void Sync_Click(object sender, RoutedEventArgs e) => await SynchronizeAsync(false);

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        CaptureSettings();
        await _settings.SaveAsync(_context.DataDirectory);
        try
        {
            using var client = new WebDavClient(_settings);
            BusyText.Text = "正在测试连接...";
            BusyOverlay.Visibility = Visibility.Visible;
            var files = await client.ListFilesAsync();
            StatusText.Text = $"连接成功，远程目录中有 {files.Count} 个文件。";
            _context.SetStatus?.Invoke("WebDAV 连接测试成功。");
        }
        catch (Exception exception)
        {
            StatusText.Text = $"连接未成功：{exception.Message}";
            _context.SetStatus?.Invoke("WebDAV 连接测试未成功。");
        }
        finally
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async Task SynchronizeAsync(bool automatic)
    {
        if (_operationCancellation is not null)
        {
            return;
        }

        CaptureSettings();
        await _settings.SaveAsync(_context.DataDirectory);
        _operationCancellation = new CancellationTokenSource();
        BusyText.Text = automatic ? "正在自动同步..." : "正在同步...";
        BusyOverlay.Visibility = Visibility.Visible;
        StatusText.Text = "正在读取差异...";
        try
        {
            var progress = new Progress<string>(message =>
            {
                LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
                while (LogLines.Count > 500)
                {
                    LogLines.RemoveAt(0);
                }

                if (LogListBox.Items.Count > 0)
                {
                    LogListBox.ScrollIntoView(LogListBox.Items[^1]);
                }
            });
            var result = await _engine.SynchronizeAsync(_settings, _context.DataDirectory, progress, _operationCancellation.Token);
            foreach (var message in result.Messages)
            {
                LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            }

            LastSyncText.Text = $"最近同步：{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            StatusText.Text = result.Messages[0];
            _context.SetStatus?.Invoke("数据同步完成。");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "同步已停止。";
            _context.SetStatus?.Invoke("数据同步已停止。");
        }
        catch (Exception exception)
        {
            LogLines.Add($"[{DateTime.Now:HH:mm:ss}] 异常：{exception.Message}");
            StatusText.Text = $"同步未成功：{exception.Message}";
            _context.SetStatus?.Invoke("数据同步未成功。");
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void AutoSync_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        CaptureSettings();
        _ = _settings.SaveAsync(_context.DataDirectory);
        UpdateTimer();
    }

    private void Interval_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        CaptureSettings();
        _ = _settings.SaveAsync(_context.DataDirectory);
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        _autoSyncTimer.Stop();
        if (_settings.AutoSync)
        {
            _autoSyncTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(_settings.IntervalMinutes, 1, 1440));
            _autoSyncTimer.Start();
        }
    }

    private async void AutoSyncTimer_Tick(object? sender, EventArgs e) => await SynchronizeAsync(true);

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogLines.Clear();
}
