using System.Windows;

namespace DysonHarness.UI.Windows;

/// <summary>
/// CefSharp-bound JS object (<c>dysonShell</c>) for shell chrome updates from the page.
/// </summary>
public sealed class DysonShellBridge(MainWindow window)
{
    private readonly MainWindow _window = window ?? throw new ArgumentNullException(nameof(window));

    public void NotifyTheme(string? theme)
    {
        var dispatcher = _window.Dispatcher;
        if (dispatcher.CheckAccess())
            _window.ApplyChromeTheme(theme);
        else
            dispatcher.Invoke(() => _window.ApplyChromeTheme(theme));
    }
}
