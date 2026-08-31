using System.Windows;
using ProjectManager.Wpf.ViewModels;

namespace ProjectManager.Wpf.Views;

public partial class GroupDialog : Window
{
    private readonly IReadOnlyList<GroupChoice> _parentChoices;

    public GroupDialog(
        IEnumerable<GroupChoice> parentChoices,
        string title,
        string initialName = "",
        Guid? initialParentId = null)
    {
        InitializeComponent();
        Title = title;
        _parentChoices = parentChoices
            .Select(choice => choice.Id is null ? new GroupChoice(null, "顶级分组") : choice)
            .ToList();
        ParentComboBox.ItemsSource = _parentChoices;
        ParentComboBox.SelectedItem = _parentChoices.FirstOrDefault(item => item.Id == initialParentId) ?? _parentChoices.First();
        NameTextBox.Text = initialName;
        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    public string GroupName { get; private set; } = string.Empty;
    public Guid? ParentGroupId { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            ValidationText.Text = "请输入分组名称。";
            ValidationBanner.Visibility = Visibility.Visible;
            NameTextBox.Focus();
            return;
        }

        GroupName = NameTextBox.Text.Trim();
        ParentGroupId = (ParentComboBox.SelectedItem as GroupChoice)?.Id;
        DialogResult = true;
    }
}
