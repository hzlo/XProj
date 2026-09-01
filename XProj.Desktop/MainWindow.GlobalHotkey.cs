using System.Windows;

namespace ProjectManager.Wpf;

public partial class MainWindow
{
    private void RegisterGlobalHotkey(string hotkey)
    {
        if (_globalHotkey.TryRegister(this, hotkey, out var error))
        {
            return;
        }

        _viewModel.SetStatus($"全局快捷键注册失败：{error}");
    }

    private void GlobalHotkey_Pressed(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_isExiting || _shutdownCompleted || !IsLoaded)
            {
                return;
            }

            if (IsVisible && WindowState != WindowState.Minimized)
            {
                CloseRunningPopover();
                Hide();
                return;
            }

            ShowFromTray();
        });
    }
}
