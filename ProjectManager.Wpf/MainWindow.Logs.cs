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
    private void ViewModelOnLogDisplayUpdated(object? sender, LogDisplayUpdateEventArgs eventArgs)
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            _logDisplayDirty = true;
            return;
        }

        var wasAtBottom = IsLogListAtBottom();
        if (eventArgs.ReplacementLines is not null)
        {
            _viewModel.DisplayedLogLines.Clear();
            foreach (var line in eventArgs.ReplacementLines)
            {
                _viewModel.DisplayedLogLines.Add(new LogLine(line));
            }
        }
        else
        {
            for (var index = 0; index < eventArgs.LinesToRemove && _viewModel.DisplayedLogLines.Count > 0; index++)
            {
                _viewModel.DisplayedLogLines.RemoveAt(0);
            }

            foreach (var line in eventArgs.LinesToAppend)
            {
                _viewModel.DisplayedLogLines.Add(new LogLine(line));
            }
        }

        if (wasAtBottom && LogListBox.Items.Count > 0)
        {
            LogListBox.ScrollIntoView(LogListBox.Items[^1]);
        }
    }

    private bool IsLogListAtBottom()
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(LogListBox);
        return scrollViewer is null || scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 1;
    }

    private void LogListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopySelectedLogLines();
            e.Handled = true;
        }
    }

    private void CopySelectedLogLines_Click(object sender, RoutedEventArgs e) => CopySelectedLogLines();

    private void CopySelectedLogLines()
    {
        var text = string.Join(Environment.NewLine, LogListBox.SelectedItems.Cast<LogLine>().Select(line => line.Text));
        if (text.Length > 0) Clipboard.SetText(text);
    }

    private void CopyAllLogLines_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, LogListBox.Items.Cast<LogLine>().Select(line => line.Text));
        if (text.Length > 0) Clipboard.SetText(text);
    }

    private void RefreshDeferredLogDisplay()
    {
        if (!_logDisplayDirty || !IsVisible || WindowState == WindowState.Minimized)
        {
            return;
        }

        _logDisplayDirty = false;
        _viewModel.RefreshLogDisplay();
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T target)
            {
                return target;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
