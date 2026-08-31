using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ProjectManager.Wpf.Models;
using ProjectManager.Wpf.ViewModels;

namespace ProjectManager.Wpf.Views;

public partial class RunPlansDialog : Window
{
    private readonly Func<RunPlan?, IReadOnlyList<RunPlanCommandChoice>> _commandChoicesFactory;
    private readonly ListCollectionView _commandChoicesView;
    private RunPlan? _selectedPlan;
    private bool _isLoadingPlanState;
    private bool _isBulkUpdatingChoices;

    public RunPlansDialog(
        IReadOnlyList<RunPlan> runPlans,
        Func<RunPlan?, IReadOnlyList<RunPlanCommandChoice>> commandChoicesFactory)
    {
        InitializeComponent();
        _commandChoicesFactory = commandChoicesFactory;
        Plans = new ObservableCollection<RunPlan>(runPlans.Select(CloneRunPlan));
        CommandChoices = new ObservableCollection<RunPlanCommandChoice>();
        _commandChoicesView = new ListCollectionView(CommandChoices)
        {
            Filter = FilterCommandChoice,
            IsLiveFiltering = false,
            IsLiveGrouping = false,
            IsLiveSorting = false
        };
        _commandChoicesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RunPlanCommandChoice.GroupKey)));
        DataContext = this;

        if (Plans.Count == 0)
        {
            Plans.Add(new RunPlan
            {
                Name = "默认方案",
                StopCommandsOutsidePlan = true
            });
        }

        Loaded += (_, _) => PlansList.SelectedItem = Plans.FirstOrDefault();
    }

    public ObservableCollection<RunPlan> Plans { get; }
    public ObservableCollection<RunPlanCommandChoice> CommandChoices { get; }
    public ICollectionView CommandChoicesView => _commandChoicesView;
    public IReadOnlyList<RunPlan>? Result { get; private set; }
    public Guid? RunPlanIdToStart { get; private set; }

    private void PlansList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveSelectedPlanState();
        _selectedPlan = PlansList.SelectedItem as RunPlan;
        LoadSelectedPlanState();
    }

    private void AddPlan_Click(object sender, RoutedEventArgs e)
    {
        SaveSelectedPlanState();
        var plan = new RunPlan
        {
            Name = $"运行方案 {Plans.Count + 1}",
            StopCommandsOutsidePlan = true,
            SortOrder = Plans.Count
        };
        Plans.Add(plan);
        PlansList.SelectedItem = plan;
        PlanNameTextBox.Focus();
        PlanNameTextBox.SelectAll();
    }

    private void DeletePlan_Click(object sender, RoutedEventArgs e)
    {
        if (PlansList.SelectedItem is not RunPlan selected)
        {
            return;
        }

        var index = Plans.IndexOf(selected);
        Plans.Remove(selected);
        _selectedPlan = null;
        UnsubscribeFromCommandChoices();
        CommandChoices.Clear();
        PlansList.SelectedItem = Plans.ElementAtOrDefault(Math.Min(index, Plans.Count - 1));
        if (Plans.Count == 0)
        {
            _isLoadingPlanState = true;
            PlanNameTextBox.Text = string.Empty;
            StopOutsideCheckBox.IsChecked = true;
            _isLoadingPlanState = false;
            RefreshCommandViewAndSummary();
        }
    }

    private void PlanNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_selectedPlan is null || _isLoadingPlanState)
        {
            return;
        }

        _selectedPlan.Name = PlanNameTextBox.Text.Trim();
        PlansList.Items.Refresh();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildResult(out var plans))
        {
            return;
        }

        Result = plans;
        DialogResult = true;
    }

    private void SaveAndRun_Click(object sender, RoutedEventArgs e)
    {
        if (PlansList.SelectedItem is not RunPlan selected)
        {
            ShowValidationMessage("请先选择一个运行方案。");
            return;
        }

        if (!TryBuildResult(out var plans))
        {
            return;
        }

        var selectedResult = plans.Single(item => item.Id == selected.Id);
        if (selectedResult.Commands.Count == 0)
        {
            ShowValidationMessage("“保存并启动”至少需要选择一条命令。");
            return;
        }

        Result = plans;
        RunPlanIdToStart = selected.Id;
        DialogResult = true;
    }

    private void SelectAllCommands_Click(object sender, RoutedEventArgs e) => SetFilteredCommandsSelected(true);

    private void ClearAllCommands_Click(object sender, RoutedEventArgs e) => SetFilteredCommandsSelected(false);

    private void SetFilteredCommandsSelected(bool isSelected)
    {
        var filteredChoices = GetFilteredCommandChoices();
        _isBulkUpdatingChoices = true;
        try
        {
            using (_commandChoicesView.DeferRefresh())
            {
                SetCommandsSelected(filteredChoices, isSelected);
            }
        }
        finally
        {
            _isBulkUpdatingChoices = false;
        }
        RefreshCommandViewAndSummary();
        ShowBatchSelectionMessage(isSelected, filteredChoices.Count, "当前筛选结果");
    }

    private void SelectProjectCommands_Click(object sender, RoutedEventArgs e) =>
        SetProjectCommandsSelected(sender, true);

    private void ClearProjectCommands_Click(object sender, RoutedEventArgs e) =>
        SetProjectCommandsSelected(sender, false);

    private void SetProjectCommandsSelected(object sender, bool isSelected)
    {
        if (sender is not Button { CommandParameter: RunPlanCommandChoiceGroupKey groupKey })
        {
            return;
        }

        var projectChoices = CommandChoices
            .Where(choice => choice.ProjectId == groupKey.ProjectId)
            .ToList();
        _isBulkUpdatingChoices = true;
        try
        {
            using (_commandChoicesView.DeferRefresh())
            {
                SetCommandsSelected(projectChoices, isSelected);
            }
        }
        finally
        {
            _isBulkUpdatingChoices = false;
        }
        RefreshCommandViewAndSummary();
        ShowBatchSelectionMessage(isSelected, projectChoices.Count, $"项目“{groupKey.ProjectDisplayName}”");
    }

    private void SetCommandsSelected(IReadOnlyList<RunPlanCommandChoice> choices, bool isSelected)
    {
        foreach (var choice in choices)
        {
            choice.IsSelected = isSelected;
        }
    }

    private List<RunPlanCommandChoice> GetFilteredCommandChoices() =>
        _commandChoicesView.Cast<RunPlanCommandChoice>().ToList();

    private void ShowBatchSelectionMessage(bool isSelected, int affectedCount, string scope)
    {
        ValidationText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        ValidationText.Text = affectedCount == 0
            ? $"{scope}中没有可操作的命令。"
            : $"已{(isSelected ? "选择" : "清空")}{scope}中的 {affectedCount} 条命令；其他命令保持不变。";
    }

    private void CommandSearchTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshCommandViewAndSummary();

    private void SelectedOnlyToggle_Changed(object sender, RoutedEventArgs e) =>
        RefreshCommandViewAndSummary();

    private bool FilterCommandChoice(object item)
    {
        if (item is not RunPlanCommandChoice choice)
        {
            return false;
        }

        return choice.MatchesSearch(CommandSearchTextBox?.Text) &&
               (SelectedOnlyToggle?.IsChecked != true || choice.IsSelected);
    }

    private void RefreshCommandViewAndSummary()
    {
        if (!IsInitialized)
        {
            return;
        }

        _commandChoicesView.Refresh();
        var selectedCount = CommandChoices.Count(choice => choice.IsSelected);
        var totalCount = CommandChoices.Count;
        var filteredCount = _commandChoicesView.Cast<object>().Count();
        SelectionStatsText.Text = $"已选 {selectedCount} / 共 {totalCount}";
        SelectionStatsText.ToolTip = filteredCount == totalCount
            ? "当前显示全部命令"
            : $"当前筛选显示 {filteredCount} 条命令";
        SelectFilteredCommandsButton.IsEnabled = filteredCount > 0;
        ClearFilteredCommandsButton.IsEnabled = filteredCount > 0;
        EmptyCommandResultsText.Visibility = filteredCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CommandRow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: RunPlanCommandChoice choice } ||
            IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        choice.IsSelected = !choice.IsSelected;
        RefreshCommandViewAndSummary();
        e.Handled = true;
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is CheckBox or TextBox or Button)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private bool TryBuildResult(out IReadOnlyList<RunPlan> plans)
    {
        SaveSelectedPlanState();
        ValidationText.Text = string.Empty;
        if (Plans.Count == 0)
        {
            ShowValidationMessage("请至少保留一个运行方案。");
            plans = Array.Empty<RunPlan>();
            return false;
        }

        if (Plans.Any(item => string.IsNullOrWhiteSpace(item.Name)))
        {
            ShowValidationMessage("运行方案名称不能为空。");
            plans = Array.Empty<RunPlan>();
            return false;
        }

        plans = Plans.Select((item, index) =>
        {
            var clone = CloneRunPlan(item);
            clone.SortOrder = index;
            return clone;
        }).ToList();
        return true;
    }

    private void LoadSelectedPlanState()
    {
        _isLoadingPlanState = true;
        UnsubscribeFromCommandChoices();
        CommandChoices.Clear();
        try
        {
            if (_selectedPlan is null)
            {
                PlanNameTextBox.Text = string.Empty;
                StopOutsideCheckBox.IsChecked = true;
                return;
            }

            PlanNameTextBox.Text = _selectedPlan.Name;
            StopOutsideCheckBox.IsChecked = _selectedPlan.StopCommandsOutsidePlan;
            foreach (var choice in _commandChoicesFactory(_selectedPlan))
            {
                choice.PropertyChanged += CommandChoice_PropertyChanged;
                CommandChoices.Add(choice);
            }
        }
        finally
        {
            _isLoadingPlanState = false;
            RefreshCommandViewAndSummary();
        }
    }

    private void CommandChoice_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RunPlanCommandChoice.IsSelected) &&
            !_isLoadingPlanState &&
            !_isBulkUpdatingChoices)
        {
            RefreshCommandViewAndSummary();
        }
    }

    private void UnsubscribeFromCommandChoices()
    {
        foreach (var choice in CommandChoices)
        {
            choice.PropertyChanged -= CommandChoice_PropertyChanged;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        UnsubscribeFromCommandChoices();
        base.OnClosed(e);
    }

    private void SaveSelectedPlanState()
    {
        if (_selectedPlan is null)
        {
            return;
        }

        _selectedPlan.Name = PlanNameTextBox.Text.Trim();
        _selectedPlan.StopCommandsOutsidePlan = StopOutsideCheckBox.IsChecked == true;
        _selectedPlan.Commands = CommandChoices
            .Where(item => item.IsSelected)
            .Select((item, index) => new RunPlanCommand
            {
                ProjectId = item.ProjectId,
                CommandId = item.CommandId,
                DelaySeconds = Math.Clamp(item.DelaySeconds, 0, 300),
                SortOrder = index
            })
            .ToList();
    }

    private void ShowValidationMessage(string message)
    {
        ValidationText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        ValidationText.Text = message;
    }

    private static RunPlan CloneRunPlan(RunPlan runPlan) => new()
    {
        Id = runPlan.Id == Guid.Empty ? Guid.NewGuid() : runPlan.Id,
        Name = runPlan.Name,
        StopCommandsOutsidePlan = runPlan.StopCommandsOutsidePlan,
        SortOrder = runPlan.SortOrder,
        Commands = runPlan.Commands
            .OrderBy(item => item.SortOrder)
            .Select(item => new RunPlanCommand
            {
                ProjectId = item.ProjectId,
                CommandId = item.CommandId,
                DelaySeconds = item.DelaySeconds,
                SortOrder = item.SortOrder
            })
            .ToList()
    };
}
