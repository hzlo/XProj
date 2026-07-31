using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ProjectManager.Wpf.Models;
using ProjectManager.Wpf.ViewModels;

namespace ProjectManager.Wpf.Views;

public partial class RunPlansDialog : Window
{
    private readonly Func<RunPlan?, IReadOnlyList<RunPlanCommandChoice>> _commandChoicesFactory;
    private RunPlan? _selectedPlan;

    public RunPlansDialog(
        IReadOnlyList<RunPlan> runPlans,
        Func<RunPlan?, IReadOnlyList<RunPlanCommandChoice>> commandChoicesFactory)
    {
        InitializeComponent();
        _commandChoicesFactory = commandChoicesFactory;
        Plans = new ObservableCollection<RunPlan>(runPlans.Select(CloneRunPlan));
        CommandChoices = new ObservableCollection<RunPlanCommandChoice>();
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
        PlansList.SelectedItem = Plans.ElementAtOrDefault(Math.Min(index, Plans.Count - 1));
        if (Plans.Count == 0)
        {
            CommandChoices.Clear();
            PlanNameTextBox.Text = string.Empty;
            StopOutsideCheckBox.IsChecked = true;
        }
    }

    private void PlanNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_selectedPlan is null)
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

        Result = plans;
        RunPlanIdToStart = selected.Id;
        DialogResult = true;
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
        CommandChoices.Clear();
        if (_selectedPlan is null)
        {
            return;
        }

        PlanNameTextBox.Text = _selectedPlan.Name;
        StopOutsideCheckBox.IsChecked = _selectedPlan.StopCommandsOutsidePlan;
        foreach (var choice in _commandChoicesFactory(_selectedPlan))
        {
            CommandChoices.Add(choice);
        }
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
