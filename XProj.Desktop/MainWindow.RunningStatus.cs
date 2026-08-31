using System.Windows;
using System.Windows.Input;

namespace ProjectManager.Wpf;

public partial class MainWindow
{
    // Running status owns the transient popover lifecycle separately from page navigation.
    private void RunningStatus_MouseEnter(object sender, MouseEventArgs e) => ShowRunningPopover();
    private void RunningStatus_MouseLeave(object sender, MouseEventArgs e) => ScheduleRunningPopoverClose();
    private void RunningPopover_MouseEnter(object sender, MouseEventArgs e) => _runningPopoverCloseTimer.Stop();
    private void RunningPopover_MouseLeave(object sender, MouseEventArgs e) => ScheduleRunningPopoverClose();

    private void RunningStatus_Click(object sender, RoutedEventArgs e)
    {
        if (RunningStatusButton.IsChecked == true) ShowRunningPopover();
        else ScheduleRunningPopoverClose();
    }

    private void RunningStatusPopup_Closed(object? sender, EventArgs e) => CloseRunningPopover();

    private void CloseRunningPopover()
    {
        _runningPopoverCloseTimer.Stop();
        RunningStatusPopup.IsOpen = false;
        RunningStatusButton.IsChecked = false;
    }

    private void ShowRunningPopover()
    {
        _runningPopoverCloseTimer.Stop();
        RunningStatusPopup.IsOpen = true;
    }

    private void ScheduleRunningPopoverClose()
    {
        if (RunningStatusButton.IsChecked == true) return;
        _runningPopoverCloseTimer.Stop();
        _runningPopoverCloseTimer.Start();
    }

    private void CloseRunningPopoverIfPointerLeft()
    {
        _runningPopoverCloseTimer.Stop();
        if (RunningStatusButton.IsChecked != true && !RunningStatusButton.IsMouseOver && !RunningPopoverSurface.IsMouseOver)
        {
            RunningStatusPopup.IsOpen = false;
        }
    }
}
