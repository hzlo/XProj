using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ProjectManager.Wpf.Models;

namespace ProjectManager.Wpf.Infrastructure;

public static class ThemeManager
{
    private static readonly Uri DarkPalette = new("Themes/AppleDark.xaml", UriKind.Relative);
    private static readonly Uri LightPalette = new("Themes/AppleLight.xaml", UriKind.Relative);
    private static readonly string[] NeutralBrushKeys =
    [
        "WindowBrush", "PanelBrush", "PanelRaisedBrush", "PanelSolidBrush", "HoverBrush", "PressedBrush",
        "BorderBrush", "SoftBorderBrush", "TextBrush", "SecondaryTextBrush", "MutedTextBrush",
        "InsetPanelBrush", "InputBrush", "InputFocusedBrush", "ReadOnlyBrush", "HoverBorderBrush",
        "SubtleHoverBrush", "PopupBrush", "TableHeaderBrush", "AlternatingRowBrush", "ScrollThumbBrush",
        "TooltipBrush", "ContextMenuBrush", "LogBackgroundBrush", "LogTextBrush", "TitleStatusBrush",
        "SidebarBrush", "FooterTextBrush", "AppBackdropBrush"
    ];

    public static bool IsDark { get; private set; } = true;

    public static void Apply(AppSettings settings)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        IsDark = settings.Theme != "Light";
        var dictionaries = application.Resources.MergedDictionaries;
        var paletteIndex = dictionaries
            .Select((dictionary, index) => (dictionary, index))
            .FirstOrDefault(item => item.dictionary.Source?.OriginalString.Contains("AppleDark.xaml", StringComparison.OrdinalIgnoreCase) == true ||
                                    item.dictionary.Source?.OriginalString.Contains("AppleLight.xaml", StringComparison.OrdinalIgnoreCase) == true)
            .index;
        dictionaries[paletteIndex] = new ResourceDictionary { Source = IsDark ? DarkPalette : LightPalette };

        ApplyThemeColors(application.Resources, settings);
        var uiFontFamily = new FontFamily(settings.UiFontFamily);
        application.Resources["UiFontFamily"] = uiFontFamily;
        application.Resources["LogFontFamily"] = new FontFamily(settings.LogFontFamily);
        application.Resources["LogFontSize"] = settings.LogFontSize;
        application.Resources["UiFontSize"] = settings.UiFontSize;
        ApplyUiFontScale(application.Resources, settings.UiFontSize);

        foreach (Window window in application.Windows)
        {
            window.FontFamily = uiFontFamily;
            window.FontSize = settings.UiFontSize;
            WindowBackdrop.Apply(window, IsDark);
        }
    }

    private static void ApplyUiFontScale(ResourceDictionary resources, double baseSize)
    {
        resources["UiFontSizePageTitle"] = baseSize + 5;
        resources["UiFontSizeDialogTitle"] = baseSize + 3;
        resources["UiFontSizeSectionTitle"] = baseSize + 1;
        resources["UiFontSizeFieldLabel"] = baseSize - 1;
        resources["UiFontSizeCaption"] = baseSize - 2;
    }

    public static bool TryNormalizeColor(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length != 7 || value[0] != '#' ||
            !byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
        {
            return false;
        }

        normalized = $"#{red:X2}{green:X2}{blue:X2}";
        return true;
    }

    public static bool HasReadableContrast(string foreground, string background) =>
        TryParseColor(foreground, out var foregroundColor) &&
        TryParseColor(background, out var backgroundColor) &&
        ContrastRatio(foregroundColor, backgroundColor) >= 4.5;

    public static bool AreThemeColorsValid(AppSettings settings) =>
        TryNormalizeColor(settings.LightForegroundColor, out _) &&
        TryNormalizeColor(settings.LightBackgroundColor, out _) &&
        TryNormalizeColor(settings.DarkForegroundColor, out _) &&
        TryNormalizeColor(settings.DarkBackgroundColor, out _) &&
        HasReadableContrast(settings.LightForegroundColor, settings.LightBackgroundColor) &&
        HasReadableContrast(settings.DarkForegroundColor, settings.DarkBackgroundColor);

    public static void NormalizeThemeColors(AppSettings settings)
    {
        (settings.LightForegroundColor, settings.LightBackgroundColor) = NormalizePair(
            settings.LightForegroundColor,
            settings.LightBackgroundColor,
            AppSettings.DefaultLightForegroundColor,
            AppSettings.DefaultLightBackgroundColor);
        (settings.DarkForegroundColor, settings.DarkBackgroundColor) = NormalizePair(
            settings.DarkForegroundColor,
            settings.DarkBackgroundColor,
            AppSettings.DefaultDarkForegroundColor,
            AppSettings.DefaultDarkBackgroundColor);
    }

    private static void ApplyThemeColors(ResourceDictionary resources, AppSettings settings)
    {
        ClearThemeColorOverrides(resources);
        var foregroundText = IsDark ? settings.DarkForegroundColor : settings.LightForegroundColor;
        var backgroundText = IsDark ? settings.DarkBackgroundColor : settings.LightBackgroundColor;
        var defaultForeground = IsDark ? AppSettings.DefaultDarkForegroundColor : AppSettings.DefaultLightForegroundColor;
        var defaultBackground = IsDark ? AppSettings.DefaultDarkBackgroundColor : AppSettings.DefaultLightBackgroundColor;
        if (foregroundText.Equals(defaultForeground, StringComparison.OrdinalIgnoreCase) &&
            backgroundText.Equals(defaultBackground, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!TryParseColor(foregroundText, out var foreground) || !TryParseColor(backgroundText, out var background))
        {
            return;
        }

        if (IsDark)
        {
            ApplyDarkColors(resources, foreground, background);
        }
        else
        {
            ApplyLightColors(resources, foreground, background);
        }
    }

    private static void ApplyDarkColors(ResourceDictionary resources, Color foreground, Color background)
    {
        SetBrush(resources, "WindowBrush", background);
        SetBrush(resources, "PanelBrush", Blend(background, foreground, 0.06));
        SetBrush(resources, "PanelRaisedBrush", Blend(background, foreground, 0.12));
        SetBrush(resources, "PanelSolidBrush", Blend(background, foreground, 0.08));
        SetBrush(resources, "HoverBrush", Blend(background, foreground, 0.16));
        SetBrush(resources, "PressedBrush", Blend(background, foreground, 0.22));
        SetBrush(resources, "BorderBrush", Blend(background, foreground, 0.18));
        SetBrush(resources, "SoftBorderBrush", Blend(background, foreground, 0.12));
        SetTextBrushes(resources, foreground, background, 0.86, 0.62);
        SetBrush(resources, "InsetPanelBrush", Blend(background, foreground, 0.04));
        SetBrush(resources, "InputBrush", Blend(background, foreground, 0.06));
        SetBrush(resources, "InputFocusedBrush", Blend(background, foreground, 0.075));
        SetBrush(resources, "ReadOnlyBrush", Blend(background, foreground, 0.025));
        SetBrush(resources, "HoverBorderBrush", Blend(background, foreground, 0.28));
        SetBrush(resources, "SubtleHoverBrush", Blend(background, foreground, 0.14), 136);
        SetBrush(resources, "PopupBrush", Blend(background, foreground, 0.10), 250);
        SetBrush(resources, "TableHeaderBrush", Blend(background, foreground, 0.10), 238);
        SetBrush(resources, "AlternatingRowBrush", Blend(background, foreground, 0.06), 56);
        SetBrush(resources, "TooltipBrush", Blend(background, foreground, 0.15), 250);
        SetBrush(resources, "ContextMenuBrush", Blend(background, foreground, 0.10), 252);
        SetBrush(resources, "LogBackgroundBrush", Blend(background, foreground, 0.02));
        SetBrush(resources, "LogTextBrush", Blend(foreground, Color.FromRgb(139, 196, 154), 0.18));
        SetBrush(resources, "TitleStatusBrush", Blend(background, foreground, 0.10));
        SetBrush(resources, "SidebarBrush", Blend(background, foreground, 0.05));
        SetBackdrop(resources, background, foreground, true);
    }

    private static void ApplyLightColors(ResourceDictionary resources, Color foreground, Color background)
    {
        SetBrush(resources, "WindowBrush", background);
        SetBrush(resources, "PanelBrush", Blend(background, Colors.White, 0.15), 239);
        SetBrush(resources, "PanelRaisedBrush", Blend(background, Colors.White, 0.25));
        SetBrush(resources, "PanelSolidBrush", Blend(background, Colors.White, 0.12));
        SetBrush(resources, "HoverBrush", Blend(background, foreground, 0.04));
        SetBrush(resources, "PressedBrush", Blend(background, foreground, 0.09));
        SetBrush(resources, "BorderBrush", Blend(background, foreground, 0.14));
        SetBrush(resources, "SoftBorderBrush", Blend(background, foreground, 0.075));
        SetTextBrushes(resources, foreground, background, 0.72, 0.52);
        SetBrush(resources, "InsetPanelBrush", Blend(background, Colors.White, 0.12));
        SetBrush(resources, "InputBrush", Blend(background, Colors.White, 0.24));
        SetBrush(resources, "InputFocusedBrush", Blend(background, Colors.White, 0.34));
        SetBrush(resources, "ReadOnlyBrush", Blend(background, foreground, 0.03));
        SetBrush(resources, "HoverBorderBrush", Blend(background, foreground, 0.28));
        SetBrush(resources, "SubtleHoverBrush", Blend(background, foreground, 0.04), 153);
        SetBrush(resources, "PopupBrush", Blend(background, Colors.White, 0.25), 252);
        SetBrush(resources, "TableHeaderBrush", background, 245);
        SetBrush(resources, "AlternatingRowBrush", Blend(background, Colors.White, 0.08), 102);
        SetBrush(resources, "TooltipBrush", Blend(background, Colors.White, 0.25), 252);
        SetBrush(resources, "ContextMenuBrush", Blend(background, Colors.White, 0.25), 252);
        SetBrush(resources, "LogBackgroundBrush", Blend(background, Colors.White, 0.12));
        SetBrush(resources, "LogTextBrush", Blend(foreground, Color.FromRgb(36, 92, 49), 0.20));
        SetBrush(resources, "TitleStatusBrush", Blend(background, Colors.White, 0.25), 239);
        SetBrush(resources, "SidebarBrush", Blend(background, Colors.White, 0.25), 239);
        SetBackdrop(resources, background, foreground, false);
    }

    private static void SetTextBrushes(
        ResourceDictionary resources,
        Color foreground,
        Color background,
        double secondaryAmount,
        double mutedAmount)
    {
        SetBrush(resources, "TextBrush", foreground);
        SetBrush(resources, "SecondaryTextBrush", Blend(background, foreground, secondaryAmount));
        var muted = Blend(background, foreground, mutedAmount);
        SetBrush(resources, "MutedTextBrush", muted);
        SetBrush(resources, "FooterTextBrush", Blend(background, foreground, mutedAmount * 0.78));
        SetBrush(resources, "ScrollThumbBrush", muted, 102);
        resources[SystemColors.HighlightTextBrushKey] = CreateBrush(foreground);
    }

    private static void SetBackdrop(ResourceDictionary resources, Color background, Color foreground, bool dark)
    {
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        gradient.GradientStops.Add(new GradientStop(Blend(background, dark ? Color.FromRgb(58, 74, 117) : Colors.White, dark ? 0.10 : 0.06), 0));
        gradient.GradientStops.Add(new GradientStop(Blend(background, dark ? Colors.Black : foreground, dark ? 0.12 : 0.025), 0.55));
        gradient.GradientStops.Add(new GradientStop(Blend(background, Color.FromRgb(96, 62, 85), dark ? 0.08 : 0.025), 1));
        gradient.Freeze();
        resources["AppBackdropBrush"] = gradient;
    }

    private static void ClearThemeColorOverrides(ResourceDictionary resources)
    {
        foreach (var key in NeutralBrushKeys)
        {
            resources.Remove(key);
        }
        resources.Remove(SystemColors.HighlightTextBrushKey);
    }

    private static void SetBrush(ResourceDictionary resources, string key, Color color, byte alpha = 255) =>
        resources[key] = CreateBrush(Color.FromArgb(alpha, color.R, color.G, color.B));

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Blend(Color start, Color end, double amount) => Color.FromRgb(
        (byte)Math.Round(start.R + (end.R - start.R) * amount),
        (byte)Math.Round(start.G + (end.G - start.G) * amount),
        (byte)Math.Round(start.B + (end.B - start.B) * amount));

    private static (string Foreground, string Background) NormalizePair(
        string foreground,
        string background,
        string defaultForeground,
        string defaultBackground)
    {
        if (!TryNormalizeColor(foreground, out var normalizedForeground) ||
            !TryNormalizeColor(background, out var normalizedBackground) ||
            !HasReadableContrast(normalizedForeground, normalizedBackground))
        {
            return (defaultForeground, defaultBackground);
        }

        return (normalizedForeground, normalizedBackground);
    }

    private static bool TryParseColor(string value, out Color color)
    {
        color = default;
        if (!TryNormalizeColor(value, out var normalized))
        {
            return false;
        }

        color = Color.FromRgb(
            byte.Parse(normalized.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(normalized.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(normalized.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return true;
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color) =>
        0.2126 * LinearChannel(color.R) + 0.7152 * LinearChannel(color.G) + 0.0722 * LinearChannel(color.B);

    private static double LinearChannel(byte channel)
    {
        var value = channel / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
