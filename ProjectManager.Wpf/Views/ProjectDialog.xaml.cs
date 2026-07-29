using System.Collections.ObjectModel;
using System.Windows;
using ProjectManager.Wpf.Models;
using ProjectManager.Wpf.ViewModels;
using Forms = System.Windows.Forms;

namespace ProjectManager.Wpf.Views;

public partial class ProjectDialog : Window
{
    private readonly Guid _projectId;
    private readonly IReadOnlyList<GroupChoice> _groupChoices;

    public ProjectDialog(
        IEnumerable<GroupChoice> groupChoices,
        string title,
        ManagedProject? project = null,
        Guid? defaultGroupId = null)
    {
        InitializeComponent();
        Title = title;
        _projectId = project?.Id ?? Guid.NewGuid();
        _groupChoices = groupChoices.ToList();
        GroupComboBox.ItemsSource = _groupChoices;

        NameTextBox.Text = project?.Name ?? string.Empty;
        PathTextBox.Text = project?.WorkingDirectory ?? string.Empty;
        var selectedGroupId = project?.GroupId ?? defaultGroupId;
        GroupComboBox.SelectedItem = _groupChoices.FirstOrDefault(item => item.Id == selectedGroupId) ?? _groupChoices.First();

        Commands = new ObservableCollection<ProjectCommandDraft>(
            project?.Commands.Select(command => new ProjectCommandDraft
            {
                Id = command.Id,
                Name = command.Name,
                CommandText = command.CommandText
            }) ?? Array.Empty<ProjectCommandDraft>());
        DataContext = this;

        Loaded += (_, _) => NameTextBox.Focus();
    }

    public ObservableCollection<ProjectCommandDraft> Commands { get; }
    public ManagedProject? Result { get; private set; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择项目工作目录",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(PathTextBox.Text) ? PathTextBox.Text : string.Empty,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            PathTextBox.Text = dialog.SelectedPath;
            if (string.IsNullOrWhiteSpace(NameTextBox.Text))
            {
                NameTextBox.Text = new DirectoryInfo(dialog.SelectedPath).Name;
            }
        }
    }

    private void AddCommand_Click(object sender, RoutedEventArgs e)
    {
        var draft = new ProjectCommandDraft { Name = "启动", CommandText = string.Empty };
        Commands.Add(draft);
        CommandsList.SelectedItem = draft;
        CommandsList.ScrollIntoView(draft);
    }

    private void RemoveCommand_Click(object sender, RoutedEventArgs e)
    {
        if (CommandsList.SelectedItem is ProjectCommandDraft selected)
        {
            Commands.Remove(selected);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidationBanner.Visibility = Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            ShowValidationMessage("请输入项目名称。");
            return;
        }

        if (!Directory.Exists(PathTextBox.Text.Trim()))
        {
            ShowValidationMessage("请选择有效的工作目录。");
            return;
        }

        if (Commands.Any(item => string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.CommandText)))
        {
            ShowValidationMessage("命令名称和命令内容不能为空。");
            return;
        }

        Result = new ManagedProject
        {
            Id = _projectId,
            Name = NameTextBox.Text.Trim(),
            WorkingDirectory = Path.GetFullPath(PathTextBox.Text.Trim()),
            GroupId = (GroupComboBox.SelectedItem as GroupChoice)?.Id,
            Commands = Commands.Select(item => new ProjectCommand
            {
                Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id,
                Name = item.Name.Trim(),
                CommandText = item.CommandText.Trim()
            }).ToList()
        };
        DialogResult = true;
    }

    private void ShowValidationMessage(string message)
    {
        ValidationText.Text = message;
        ValidationBanner.Visibility = Visibility.Visible;
    }
}

public sealed class ProjectCommandDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string CommandText { get; set; } = string.Empty;
}
