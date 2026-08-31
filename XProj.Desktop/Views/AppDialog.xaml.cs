using System.Windows;
using System.Windows.Media;
using Material.Icons;

namespace ProjectManager.Wpf.Views;

public enum AppDialogKind
{
    Information,
    Warning,
    Error
}

public partial class AppDialog : Window
{
    private AppDialog(string title, string heading, string message, AppDialogKind kind, string primaryText, bool showCancel)
    {
        InitializeComponent();
        Title = title;
        Heading = heading;
        Message = message;
        (IconKind, IconBrush, IconBackground) = kind switch
        {
            AppDialogKind.Error => (MaterialIconKind.AlertCircleOutline, Brush("#FFFF6961"), Brush("#33FF453A")),
            AppDialogKind.Warning => (MaterialIconKind.AlertOutline, Brush("#FFFFB340"), Brush("#33FF9F0A")),
            _ => (MaterialIconKind.InformationOutline, Brush("#FF64A9FF"), Brush("#330A84FF"))
        };
        PrimaryButton.Content = primaryText;
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        if (kind == AppDialogKind.Error || kind == AppDialogKind.Warning && showCancel)
        {
            PrimaryButton.Style = (Style)FindResource("DangerButton");
        }

        DataContext = this;
    }

    public string Heading { get; }
    public string Message { get; }
    public MaterialIconKind IconKind { get; }
    public Brush IconBrush { get; }
    public Brush IconBackground { get; }

    public static void Show(Window owner, string title, string message, AppDialogKind kind = AppDialogKind.Information)
    {
        var dialog = new AppDialog(title, title, message, kind, "知道了", false) { Owner = owner };
        _ = dialog.ShowDialog();
    }

    public static bool Confirm(
        Window owner,
        string title,
        string message,
        string primaryText = "继续",
        AppDialogKind kind = AppDialogKind.Warning)
    {
        var dialog = new AppDialog(title, title, message, kind, primaryText, true) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private void PrimaryButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
