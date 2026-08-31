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
    private void SortableItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _dragSourceItem = ResolveSortableDataContext(sender, e.OriginalSource as DependencyObject);
    }

    private void SortableItem_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSourceItem is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPosition = e.GetPosition(null);
        if (Math.Abs(currentPosition.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPosition.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (!IsSortableSource(sender, _dragSourceItem))
        {
            return;
        }

        DragDrop.DoDragDrop((DependencyObject)sender, _dragSourceItem, DragDropEffects.Move);
        _dragSourceItem = null;
        e.Handled = true;
    }

    private async void SortableItem_Drop(object sender, DragEventArgs e)
    {
        var target = ResolveSortableDataContext(sender, e.OriginalSource as DependencyObject);
        if (_dragSourceItem is null || target is null || ReferenceEquals(_dragSourceItem, target))
        {
            return;
        }

        try
        {
            switch (sender)
            {
                case TreeView when _dragSourceItem is WorkspaceTreeItem sourceWorkspace &&
                    target is WorkspaceTreeItem targetWorkspace:
                    if (sourceWorkspace.Kind == WorkspaceTreeItemKind.Group &&
                        targetWorkspace.Kind == WorkspaceTreeItemKind.Group &&
                        sourceWorkspace.GroupId.HasValue &&
                        targetWorkspace.GroupId.HasValue)
                    {
                        await _viewModel.ReorderGroupAsync(
                            sourceWorkspace.GroupId.Value,
                            targetWorkspace.GroupId.Value,
                            ShouldInsertAfter<TreeViewItem>(e));
                    }
                    else if (sourceWorkspace.Kind == WorkspaceTreeItemKind.Project &&
                             targetWorkspace.Kind == WorkspaceTreeItemKind.Project &&
                             sourceWorkspace.Project is not null &&
                             targetWorkspace.Project is not null)
                    {
                        await _viewModel.ReorderProjectAsync(
                            sourceWorkspace.Project.Id,
                            targetWorkspace.Project.Id,
                            ShouldInsertAfter<TreeViewItem>(e));
                    }
                    break;

                case ListBox when _dragSourceItem is ManagedProject sourceProject &&
                    target is ManagedProject targetProject:
                    await _viewModel.ReorderProjectAsync(
                        sourceProject.Id,
                        targetProject.Id,
                        ShouldInsertAfter<ListBoxItem>(e));
                    break;

                case ItemsControl when _dragSourceItem is CommandRuntimeViewModel sourceCommand &&
                    target is CommandRuntimeViewModel targetCommand:
                    await _viewModel.ReorderCommandAsync(
                        sourceCommand.Command.Id,
                        targetCommand.Command.Id,
                        ShouldInsertAfter<Border>(e, horizontal: true));
                    break;
            }
        }
        catch (Exception exception)
        {
            ShowError("排序失败", exception);
        }
        finally
        {
            _dragSourceItem = null;
            e.Handled = true;
        }
    }

    private static object? ResolveSortableDataContext(object sender, DependencyObject? source)
    {
        return sender switch
        {
            TreeView => FindDataContext<WorkspaceTreeItem>(source),
            ListBox => FindDataContext<ManagedProject>(source),
            ItemsControl => FindDataContext<CommandRuntimeViewModel>(source),
            _ => null
        };
    }

    private static bool IsSortableSource(object sender, object sourceItem)
    {
        return sender switch
        {
            TreeView => sourceItem is WorkspaceTreeItem { Kind: WorkspaceTreeItemKind.Group or WorkspaceTreeItemKind.Project },
            ListBox => sourceItem is ManagedProject,
            ItemsControl => sourceItem is CommandRuntimeViewModel,
            _ => false
        };
    }

    private static DependencyObject? GetParentObject(DependencyObject source)
    {
        if (source is Visual or Visual3D)
        {
            return VisualTreeHelper.GetParent(source);
        }

        if (source is ContentElement contentElement)
        {
            return ContentOperations.GetParent(contentElement);
        }

        if (source is FrameworkContentElement frameworkContentElement)
        {
            return frameworkContentElement.Parent;
        }

        return null;
    }

    private static T? FindDataContext<T>(DependencyObject? source)
        where T : class
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: T dataContext })
            {
                return dataContext;
            }

            if (source is FrameworkContentElement { DataContext: T contentDataContext })
            {
                return contentDataContext;
            }

            source = GetParentObject(source);
        }

        return null;
    }

    private static bool ShouldInsertAfter<TContainer>(DragEventArgs e, bool horizontal = false)
        where TContainer : FrameworkElement
    {
        var container = FindAncestor<TContainer>(e.OriginalSource as DependencyObject);
        if (container is null)
        {
            return false;
        }

        var position = e.GetPosition(container);
        return horizontal
            ? position.X > container.ActualWidth / 2
            : position.Y > container.ActualHeight / 2;
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T ancestor)
            {
                return ancestor;
            }

            source = GetParentObject(source);
        }

        return null;
    }
}
