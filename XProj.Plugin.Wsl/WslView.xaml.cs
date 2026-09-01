using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using XProj.Plugin.Abstractions;

namespace XProj.Plugin.Wsl;

public partial class WslView : UserControl, System.ComponentModel.INotifyPropertyChanged
{
    private const int MaximumOutputLines = 5000;

    private readonly WslService _service = new();
    private readonly PluginHostContext _context;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _distributionOperationLock = new(1, 1);
    private WslCommandSession? _shell;
    private string? _shellDistribution;
    private TaskCompletionSource<int>? _commandCompletion;
    private bool _commandInFlight;
    private bool _distributionOperationInProgress;
    private bool _isRefreshing;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public WslView(PluginHostContext context)
    {
        InitializeComponent();
        _context = context;
        _dispatcher = Dispatcher;
        DataContext = this;
    }

    public ObservableCollection<string> OutputLines { get; } = new();

    public bool IsCommandRunning => _commandInFlight;

    public bool HasSelectedDistribution => DistributionList.SelectedItem is WslDistribution;

    public bool CanStart => DistributionList.SelectedItem is WslDistribution { IsRunning: false } &&
                            !_distributionOperationInProgress &&
                            !_isRefreshing;

    public bool CanStop => DistributionList.SelectedItem is WslDistribution { IsRunning: true } &&
                           !_distributionOperationInProgress &&
                           !_isRefreshing;

    public bool CanRunCommand => DistributionList.SelectedItem is WslDistribution { IsRunning: true } &&
                                  !_distributionOperationInProgress &&
                                  !_isRefreshing;

    public bool CanManage => DistributionList.SelectedItem is WslDistribution &&
                             !_distributionOperationInProgress &&
                             !_isRefreshing;

    public bool CanSetDefault => DistributionList.SelectedItem is WslDistribution { IsDefault: false } &&
                                 !_distributionOperationInProgress &&
                                 !_isRefreshing;

    public bool IsBusy => _distributionOperationInProgress;

    public bool HasCancellableOperation => _distributionOperationInProgress &&
                                           _managementStatusText is not null &&
                                           _exportCancellation is not null &&
                                           !_exportCancellation.IsCancellationRequested;

    public string BusyText => _managementStatusText ?? "正在处理...";

    public Task InitializeAsync() => RefreshAsync();

    public async Task ShutdownAsync()
    {
        await _distributionOperationLock.WaitAsync();
        try
        {
            _distributionOperationInProgress = true;
            NotifyControlStateChanged();
            await DisposeShellAsync();
        }
        finally
        {
            _distributionOperationInProgress = false;
            NotifyControlStateChanged();
            _distributionOperationLock.Release();
        }
    }

    private async Task StartSelectedDistributionAsync()
    {
        await _distributionOperationLock.WaitAsync();
        try
        {
            if (DistributionList.SelectedItem is not WslDistribution { IsRunning: false } distribution)
            {
                return;
            }

            _distributionOperationInProgress = true;
            NotifyControlStateChanged();
            ErrorText.Text = string.Empty;
            CommandStatusText.Text = $"正在启动：{distribution.Name}";
            await EnsureShellAsync(distribution.Name);
            await RefreshAsync();
            if (_shell is not null && !_shell.HasExited)
            {
                CommandStatusText.Text = "会话已就绪";
                _context.SetStatus?.Invoke($"已启动 WSL 发行版：{distribution.Name}");
            }
        }
        finally
        {
            _distributionOperationInProgress = false;
            NotifyControlStateChanged();
            _distributionOperationLock.Release();
        }
    }

    private async Task StopSelectedDistributionAsync()
    {
        await _distributionOperationLock.WaitAsync();
        try
        {
            if (DistributionList.SelectedItem is not WslDistribution { IsRunning: true } distribution)
            {
                return;
            }

            _distributionOperationInProgress = true;
            NotifyControlStateChanged();
            ErrorText.Text = string.Empty;
            CommandStatusText.Text = $"正在停止：{distribution.Name}";
            if (_shellDistribution == distribution.Name)
            {
                await DisposeShellAsync();
            }

            await _service.TerminateDistributionAsync(distribution.Name);
            await RefreshAsync();
            CommandStatusText.Text = "发行版已停止";
            _context.SetStatus?.Invoke($"已停止 WSL 发行版：{distribution.Name}");
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
            CommandStatusText.Text = "发行版停止失败";
        }
        finally
        {
            _distributionOperationInProgress = false;
            NotifyControlStateChanged();
            _distributionOperationLock.Release();
        }
    }

    private async void StartWsl_Click(object sender, RoutedEventArgs e) => await StartSelectedDistributionAsync();

    private async void StopWsl_Click(object sender, RoutedEventArgs e) => await StopSelectedDistributionAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (DistributionList.SelectedItem is not WslDistribution { IsDefault: false } distribution)
        {
            return;
        }

        await RunManagementOperationAsync(
            $"正在设为默认：{distribution.Name}",
            async token =>
            {
                await _service.SetDefaultDistributionAsync(distribution.Name, token);
                return $"已将 {distribution.Name} 设为默认发行版";
            });
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (DistributionList.SelectedItem is not WslDistribution distribution)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"导出 {distribution.Name}",
            FileName = $"{distribution.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.tar",
            Filter = "tar 归档 (*.tar)|*.tar|全部文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        var filePath = dialog.FileName;
        await RunManagementOperationAsync(
            $"正在导出：{distribution.Name}",
            async token =>
            {
                await _service.ExportDistributionAsync(distribution.Name, filePath, token);
                return $"已导出到 {filePath}";
            });
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var fileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要导入的备份文件",
            Filter = "发行版备份 (*.tar;*.tar.gz;*.tar.xz;*.vhdx;*.vhd)|*.tar;*.tar.gz;*.tar.xz;*.vhdx;*.vhd|全部文件 (*.*)|*.*"
        };
        if (fileDialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        var folderDialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择发行版安装目录（本地 NTFS 磁盘）"
        };
        if (folderDialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(fileDialog.FileName);
        foreach (var character in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(character, '_');
        }

        var installLocation = Path.Combine(folderDialog.FolderName, name);
        await RunManagementOperationAsync(
            $"正在导入：{name}",
            async token =>
            {
                await _service.ImportDistributionAsync(name, installLocation, fileDialog.FileName, token);
                return $"已导入为 {name}";
            });
    }

    private async void Unregister_Click(object sender, RoutedEventArgs e)
    {
        if (DistributionList.SelectedItem is not WslDistribution distribution)
        {
            return;
        }

        var confirmed = _context.Confirm?.Invoke(
            "卸载发行版",
            $"确定卸载“{distribution.Name}”吗？\n\n该操作会永久删除此发行版内的全部数据，无法恢复。建议先导出备份。",
            "永久删除") ?? false;
        if (!confirmed)
        {
            return;
        }

        await RunManagementOperationAsync(
            $"正在卸载：{distribution.Name}",
            async token =>
            {
                await _service.UnregisterDistributionAsync(distribution.Name, token);
                return $"已卸载 {distribution.Name}";
            });
    }

    private string? _managementStatusText;
    private CancellationTokenSource? _exportCancellation;

    private async Task RunManagementOperationAsync(
        string progressText,
        Func<CancellationToken, Task<string>> operation)
    {
        await _distributionOperationLock.WaitAsync();
        try
        {
            _distributionOperationInProgress = true;
            _managementStatusText = progressText;
            _exportCancellation = new CancellationTokenSource();
            NotifyControlStateChanged();
            ErrorText.Text = string.Empty;
            CommandStatusText.Text = progressText;
            _context.SetStatus?.Invoke(progressText);
            try
            {
                var result = await operation(_exportCancellation.Token);
                _managementStatusText = null;
                NotifyControlStateChanged();
                CommandStatusText.Text = result;
                _context.SetStatus?.Invoke(result);
                await RefreshAsync();
            }
            catch (OperationCanceledException)
            {
                _managementStatusText = null;
                NotifyControlStateChanged();
                CommandStatusText.Text = "操作已取消";
                _context.SetStatus?.Invoke("WSL 管理操作已取消");
            }
            catch (Exception exception)
            {
                _managementStatusText = null;
                NotifyControlStateChanged();
                ErrorText.Text = exception.Message;
                CommandStatusText.Text = "操作失败";
            }
        }
        finally
        {
            _exportCancellation?.Dispose();
            _exportCancellation = null;
            _managementStatusText = null;
            _distributionOperationInProgress = false;
            NotifyControlStateChanged();
            _distributionOperationLock.Release();
        }
    }

    private void CancelManagement_Click(object sender, RoutedEventArgs e)
    {
        if (_exportCancellation is null || _exportCancellation.IsCancellationRequested)
        {
            return;
        }

        _exportCancellation.Cancel();
        _managementStatusText = "正在取消...";
        NotifyControlStateChanged();
        CommandStatusText.Text = "正在取消...";
    }

    private async Task RefreshAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        NotifyControlStateChanged();
        try
        {
            ErrorText.Text = string.Empty;
            AvailabilityText.Text = "正在检查 WSL...";
            if (!_service.IsAvailable)
            {
                AvailabilityText.Text = "未检测到 WSL";
                DistributionCountText.Text = string.Empty;
                DistributionList.ItemsSource = Array.Empty<WslDistribution>();
                OnPropertyChanged(nameof(HasSelectedDistribution));
                return;
            }

            var selectedName = (DistributionList.SelectedItem as WslDistribution)?.Name;
            var distributions = await _service.ListDistributionsAsync();
            DistributionList.ItemsSource = distributions;
            DistributionList.SelectedItem = distributions.FirstOrDefault(item => item.Name == selectedName)
                ?? distributions.FirstOrDefault(item => item.IsDefault)
                ?? distributions.FirstOrDefault();
            DistributionCountText.Text = $"{distributions.Count} 个";
            AvailabilityText.Text = distributions.Count == 0 ? "未安装发行版" : "WSL 已就绪";
            OnPropertyChanged(nameof(HasSelectedDistribution));
            NotifyControlStateChanged();
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
            AvailabilityText.Text = "检测失败";
        }
        finally
        {
            _isRefreshing = false;
            NotifyControlStateChanged();
        }
    }

    private async void DistributionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasSelectedDistribution));
        NotifyControlStateChanged();
        if (DistributionList.SelectedItem is WslDistribution distribution)
        {
            _context.SetStatus?.Invoke($"已选择 WSL 发行版：{distribution.Name}");
            if (_shellDistribution is not null && _shellDistribution != distribution.Name)
            {
                await DisposeShellAsync();
            }

            CommandStatusText.Text = distribution.IsRunning ? "发行版运行中" : "发行版已停止";
        }
    }

    /// <summary>
    /// 确保指定发行版存在一个存活的持久 shell；切换发行版时替换旧会话。
    /// </summary>
    private async Task EnsureShellAsync(string distribution)
    {
        if (_shell is not null &&
            _shellDistribution == distribution &&
            !_shell.HasExited)
        {
            return;
        }

        await DisposeShellAsync();
        try
        {
            var shell = _service.StartShell(distribution);
            _shell = shell;
            _shellDistribution = distribution;
            _commandInFlight = false;
            _commandCompletion = null;
            CommandStatusText.Text = "会话已就绪";
            OnPropertyChanged(nameof(IsCommandRunning));

            _ = ReadOutputAsync(shell.Process.StandardOutput, isError: false);
            _ = ReadOutputAsync(shell.Process.StandardError, isError: true);
            _ = WatchShellExitAsync(shell);
        }
        catch (Exception exception)
        {
            _shell = null;
            _shellDistribution = null;
            CommandStatusText.Text = "会话启动失败";
            ErrorText.Text = exception.Message;
        }
    }

    private async Task WatchShellExitAsync(WslCommandSession shell)
    {
        await shell.Process.WaitForExitAsync().ConfigureAwait(false);
        await _dispatcher.InvokeAsync(async () =>
        {
            if (!ReferenceEquals(_shell, shell))
            {
                return;
            }

            FailPendingCommand("会话已退出");
            await DisposeShellAsync();
            CommandStatusText.Text = "会话已退出";
            OnPropertyChanged(nameof(IsCommandRunning));
        });
    }

    private async void RunCommand_Click(object sender, RoutedEventArgs e) => await RunCommandAsync();

    private async void CommandTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RunCommandAsync();
        }
    }

    private async Task RunCommandAsync()
    {
        if (DistributionList.SelectedItem is not WslDistribution distribution ||
            !distribution.IsRunning ||
            string.IsNullOrWhiteSpace(CommandTextBox.Text))
        {
            return;
        }

        await EnsureShellAsync(distribution.Name);
        if (_shell is null || _shell.HasExited)
        {
            return;
        }

        if (_commandInFlight)
        {
            return;
        }

        var command = CommandTextBox.Text.Trim();
        CommandTextBox.Clear();
        _commandInFlight = true;
        _commandCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnPropertyChanged(nameof(IsCommandRunning));
        CommandStatusText.Text = $"正在运行：{command}";
        _context.SetStatus?.Invoke($"正在运行 WSL 命令：{distribution.Name}");

        try
        {
            if (OutputLines.Count > 0)
            {
                OutputLines.Add(string.Empty);
                OutputLines.Add("──────────────────────────────");
            }
            await _shell.Process.StandardInput.WriteAsync(WslService.FormatCommand(command));
        }
        catch (IOException)
        {
            FailPendingCommand("会话已退出");
            return;
        }

        var exitCode = await _commandCompletion.Task;
        if (!_commandInFlight)
        {
            return;
        }

        _commandInFlight = false;
        _commandCompletion = null;
        CommandStatusText.Text = exitCode == 0
            ? $"完成：{command}"
            : $"完成（代码 {exitCode}）：{command}";
        OnPropertyChanged(nameof(IsCommandRunning));
    }

    private void FailPendingCommand(string message)
    {
        if (!_commandInFlight)
        {
            return;
        }

        _commandInFlight = false;
        _commandCompletion?.TrySetResult(-1);
        _commandCompletion = null;
        CommandStatusText.Text = message;
        OnPropertyChanged(nameof(IsCommandRunning));
    }

    private async Task ReadOutputAsync(StreamReader reader, bool isError)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith(WslService.ExitMarkerPrefix, StringComparison.Ordinal))
            {
                if (!isError)
                {
                    var rawCode = line[WslService.ExitMarkerPrefix.Length..].Trim();
                    _ = int.TryParse(rawCode, out var exitCode);
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (_commandInFlight)
                        {
                            _commandCompletion?.TrySetResult(exitCode);
                        }
                    });
                }
                continue;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                OutputLines.Add(isError ? $"[错误] {line}" : line);
                TrimOutputIfNeeded();
                if (OutputLines.Count > 0)
                {
                    OutputListBox.ScrollIntoView(OutputLines[^1]);
                }
            });
        }
    }

    private void TrimOutputIfNeeded()
    {
        while (OutputLines.Count > MaximumOutputLines)
        {
            OutputLines.RemoveAt(0);
        }
    }

    private void ClearOutput_Click(object sender, RoutedEventArgs e) => OutputLines.Clear();

    private void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (DistributionList.SelectedItem is not WslDistribution distribution)
        {
            return;
        }

        try
        {
            _service.OpenTerminal(distribution.Name);
            _context.SetStatus?.Invoke($"已打开 WSL 终端：{distribution.Name}");
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private async void StopCommand_Click(object sender, RoutedEventArgs e) => await StopSessionAsync();

    /// <summary>
    /// 终止当前命令：结束 shell 进程树后立即重建同发行版的新会话。
    /// </summary>
    private async Task StopSessionAsync()
    {
        if (_shell is null)
        {
            return;
        }

        var distribution = _shellDistribution;
        FailPendingCommand("已终止");
        await DisposeShellAsync();
        CommandStatusText.Text = "已终止";
        _context.SetStatus?.Invoke("WSL 命令已终止");
        if (distribution is not null)
        {
            await EnsureShellAsync(distribution);
        }
    }

    private async Task DisposeShellAsync()
    {
        var shell = _shell;
        _shell = null;
        _shellDistribution = null;
        FailPendingCommand("会话已退出");
        if (shell is not null)
        {
            await shell.DisposeAsync();
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

    private void NotifyControlStateChanged()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRunCommand));
        OnPropertyChanged(nameof(CanManage));
        OnPropertyChanged(nameof(CanSetDefault));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(HasCancellableOperation));
        OnPropertyChanged(nameof(BusyText));
    }
}
