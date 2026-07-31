using System.Collections.ObjectModel;
using System.Windows;
using ProjectManager.Wpf.Models;

namespace ProjectManager.Wpf.Views;

public partial class ProjectDiscoveryDialog : Window
{
    public ProjectDiscoveryDialog(string rootDirectory, IEnumerable<ManagedProject> projects)
    {
        InitializeComponent();
        RootDirectory = rootDirectory;
        Choices = new ObservableCollection<DiscoveredProjectChoice>(
            projects.Select(project => new DiscoveredProjectChoice(project)));
        DataContext = this;
    }

    public string RootDirectory { get; }
    public ObservableCollection<DiscoveredProjectChoice> Choices { get; }
    public IReadOnlyList<ManagedProject> Result { get; private set; } = Array.Empty<ManagedProject>();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = Choices
            .Where(item => item.IsSelected)
            .Select(item => item.Project)
            .ToList();
        if (Result.Count == 0)
        {
            ValidationText.Text = "请至少选择一个项目。";
            return;
        }

        DialogResult = true;
    }
}

public sealed class DiscoveredProjectChoice
{
    public DiscoveredProjectChoice(ManagedProject project)
    {
        Project = project;
    }

    public ManagedProject Project { get; }
    public bool IsSelected { get; set; } = true;
    public string CommandSummary => string.Join(" · ", Project.Commands.Select(item => item.CommandText));
}
