using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ProjectManager.Wpf.Infrastructure;

internal static class WindowBackdrop
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmSystemBackdropType = 38;

    public static void Apply(Window window, bool useDarkMode = true)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = useDarkMode ? 1 : 0;
        SetAttribute(handle, DwmUseImmersiveDarkMode, enabled);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            if (window.AllowsTransparency)
            {
                // Transparent WPF windows already provide their own rounded clip.
                // DWM rounding/backdrop would add a second edge and produce halos.
                var noRound = 1;
                var noBackdrop = 1;
                SetAttribute(handle, DwmWindowCornerPreference, noRound);
                SetAttribute(handle, DwmSystemBackdropType, noBackdrop);
                return;
            }

            var rounded = 2;
            var mica = 2;
            SetAttribute(handle, DwmWindowCornerPreference, rounded);
            SetAttribute(handle, DwmSystemBackdropType, mica);
        }
    }

    private static void SetAttribute(IntPtr handle, int attribute, int value) =>
        _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
