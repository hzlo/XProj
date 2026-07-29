using System.Windows;
using ProjectManager.Wpf.Models;

namespace ProjectManager.Wpf.Views;

public partial class CommandDialog : Window
{
    public CommandDialog(ProjectCommand command)
    {
        InitializeComponent();
        NameTextBox.Text = command.Name;
        CommandTextBox.Text = command.CommandText;
        Loaded += (_, _) => NameTextBox.Focus();
    }

    public string CommandName { get; private set; } = string.Empty;
    public string CommandText { get; private set; } = string.Empty;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text) || string.IsNullOrWhiteSpace(CommandTextBox.Text))
        {
            ValidationText.Text = "命令名称和命令内容不能为空。";
            return;
        }

        CommandName = NameTextBox.Text.Trim();
        CommandText = CommandTextBox.Text.Trim();
        DialogResult = true;
    }
}
