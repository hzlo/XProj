using System.Windows;
using System.Windows.Media;
using ProjectManager.Wpf.Models;

namespace ProjectManager.Wpf.Infrastructure;

public static class ThemeManager
{
    private static readonly Uri DarkPalette = new("Themes/AppleDark.xaml", UriKind.Relative);
    private static readonly Uri LightPalette = new("Themes/AppleLight.xaml", UriKind.Relative);

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

        application.Resources["UiFontFamily"] = new FontFamily(settings.UiFontFamily);
        application.Resources["UiFontSize"] = settings.UiFontSize;

        foreach (Window window in application.Windows)
        {
            WindowBackdrop.Apply(window, IsDark);
        }
    }
}
