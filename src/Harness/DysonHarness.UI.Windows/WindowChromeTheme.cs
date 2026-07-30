using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DysonHarness.UI.Windows;

/// <summary>
/// Applies immersive dark title bar (DWM) and theme-matched window icons.
/// Theme source is in-app dark/light, not OS theme alone.
/// </summary>
internal static class WindowChromeTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    private static readonly Uri DarkIconUri = new("pack://application:,,,/Assets/dyson.ico");
    private static readonly Uri LightIconUri = new("pack://application:,,,/Assets/dyson-light.ico");

    public static void Apply(Window window, string? theme)
    {
        ArgumentNullException.ThrowIfNull(window);

        var dark = !string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
        window.Icon = BitmapFrame.Create(dark ? DarkIconUri : LightIconUri);

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        SetImmersiveDarkMode(hwnd, dark);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attr,
        ref int attrValue,
        int attrSize);

    private static void SetImmersiveDarkMode(IntPtr hwnd, bool enabled)
    {
        var value = enabled ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
    }
}
