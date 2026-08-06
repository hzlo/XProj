using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProjectManager.Wpf.Infrastructure;

namespace ProjectManager.Wpf.Views;

public partial class ColorPickerDialog : Window
{
    private double _hue;
    private double _saturation;
    private double _value;
    private bool _updating;

    public ColorPickerDialog(string initialColor)
    {
        InitializeComponent();
        SetColor(ThemeManager.TryNormalizeColor(initialColor, out var normalized) ? normalized : "#C0C8E4");
        Loaded += (_, _) => UpdateVisuals();
    }

    public string? ResultColor { get; private set; }

    private void SetColor(string color)
    {
        var parsed = (Color)ColorConverter.ConvertFromString(color);
        ToHsv(parsed, out _hue, out _saturation, out _value);
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        var color = FromHsv(_hue, _saturation, _value);
        SaturationSurface.Background = new SolidColorBrush(FromHsv(_hue, 1, 1));
        SaturationThumb.Margin = new Thickness(
            _saturation * Math.Max(0, SaturationSurface.ActualWidth - 18),
            14 + (1 - _value) * Math.Max(0, SaturationSurface.ActualHeight - 18),
            0,
            0);
        HueThumb.Margin = new Thickness(_hue / 360 * Math.Max(0, HueSurface.ActualWidth - 14), 0, 0, 0);
        HexValueText.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        RgbValueText.Text = $"{color.R}, {color.G}, {color.B}";
        ColorPreview.Fill = new SolidColorBrush(color);
        if (_updating) return;
        _updating = true;
        RedTextBox.Text = color.R.ToString(CultureInfo.InvariantCulture);
        GreenTextBox.Text = color.G.ToString(CultureInfo.InvariantCulture);
        BlueTextBox.Text = color.B.ToString(CultureInfo.InvariantCulture);
        _updating = false;
    }

    private void SaturationSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => UpdateSaturation(e.GetPosition(SaturationSurface));
    private void SaturationSurface_MouseMove(object sender, MouseEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) UpdateSaturation(e.GetPosition(SaturationSurface)); }
    private void HueSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => UpdateHue(e.GetPosition((UIElement)sender), (FrameworkElement)sender);
    private void HueSurface_MouseMove(object sender, MouseEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) UpdateHue(e.GetPosition((UIElement)sender), (FrameworkElement)sender); }
    private void UpdateSaturation(Point point)
    {
        _saturation = Math.Clamp(point.X / SaturationSurface.ActualWidth, 0, 1);
        _value = Math.Clamp(1 - point.Y / SaturationSurface.ActualHeight, 0, 1);
        UpdateVisuals();
    }
    private void UpdateHue(Point point, FrameworkElement surface)
    {
        if (surface.ActualWidth > 0)
        {
            _hue = Math.Clamp(point.X / surface.ActualWidth * 360, 0, 360);
            UpdateVisuals();
        }
    }
    private void RgbTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || !byte.TryParse(((TextBox)sender).Text, out var value)) return;
        var color = FromHsv(_hue, _saturation, _value);
        color = ((TextBox)sender).Tag?.ToString() switch { "R" => Color.FromRgb(value, color.G, color.B), "G" => Color.FromRgb(color.R, value, color.B), _ => Color.FromRgb(color.R, color.G, value) };
        ToHsv(color, out _hue, out _saturation, out _value); UpdateVisuals();
    }
    private void PresetColor_Click(object sender, RoutedEventArgs e) => SetColor(((Button)sender).Tag?.ToString() ?? "#C0C8E4");
    private void Apply_Click(object sender, RoutedEventArgs e) { ResultColor = HexValueText.Text; DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }

    private static Color FromHsv(double hue, double saturation, double value) { var c = value * saturation; var x = c * (1 - Math.Abs((hue / 60 % 2) - 1)); var m = value - c; var (r, g, b) = hue switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) }; return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255)); }
    private static void ToHsv(Color color, out double hue, out double saturation, out double value) { var r = color.R / 255d; var g = color.G / 255d; var b = color.B / 255d; var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b)); var delta = max - min; hue = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4); if (hue < 0) hue += 360; saturation = max == 0 ? 0 : delta / max; value = max; }
}
