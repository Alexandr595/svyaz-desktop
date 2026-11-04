using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Svyaz.App;

internal static class WindowBackdrop
{
    public static bool Apply(Window window, bool requested)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;
        var dark = 1;
        var rounded = 2;
        var border = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE: no native white border.
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        var allowed = requested && !SystemParameters.HighContrast && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            if (key?.GetValue("EnableTransparency") is int enabled && enabled == 0) allowed = false;
        var backdrop = allowed ? 3 : 1; // Desktop Acrylic / none.
        var applied = DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int)) == 0 && allowed;
        if (PresentationSource.FromVisual(window) is HwndSource source)
            source.CompositionTarget.BackgroundColor = applied ? Colors.Transparent : Color.FromRgb(11, 18, 33);
        return applied;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
