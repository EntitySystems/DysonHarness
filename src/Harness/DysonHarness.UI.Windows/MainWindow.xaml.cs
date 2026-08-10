using System.Windows;
using CefSharp;
using CefSharp.Wpf;

namespace DysonHarness.UI.Windows;

public partial class MainWindow : Window
{
    private readonly Uri _appUrl;
    private ChromiumWebBrowser? _browser;
    private string _chromeTheme = "dark";

    public MainWindow(Uri appUrl)
    {
        _appUrl = appUrl ?? throw new ArgumentNullException(nameof(appUrl));
        InitializeComponent();

        SourceInitialized += (_, _) => WindowChromeTheme.Apply(this, _chromeTheme);

        // Set LegacyBindingEnabled before the control initializes (before Address / visual tree).
        // OSR default paint rate is 30fps; raise to 60 for smoother GPU/canvas content.
        var browser = new ChromiumWebBrowser
        {
            BrowserSettings = new BrowserSettings
            {
                WindowlessFrameRate = 60,
                WebGl = CefState.Enabled,
            },
        };
        browser.JavascriptObjectRepository.Settings.LegacyBindingEnabled = true;
        browser.JavascriptObjectRepository.Register(
            "dysonShell",
            new DysonShellBridge(this),
            options: BindingOptions.DefaultBinder);

        _browser = browser;
        var handlers = new ExternalNavigationHandlers(_appUrl);
        browser.LifeSpanHandler = handlers;
        browser.RequestHandler = handlers;
        browser.FrameLoadEnd += OnFrameLoadEnd;
        browser.Address = _appUrl.AbsoluteUri;

        Root.Children.Add(browser);
        // Default dark until the page reports (matches ThemeService default).
        WindowChromeTheme.Apply(this, _chromeTheme);
    }

    internal void ApplyChromeTheme(string? theme)
    {
        var normalized = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase)
            ? "light"
            : "dark";
        if (string.Equals(_chromeTheme, normalized, StringComparison.Ordinal))
            return;

        _chromeTheme = normalized;
        WindowChromeTheme.Apply(this, _chromeTheme);
    }

    private void OnFrameLoadEnd(object? sender, FrameLoadEndEventArgs e)
    {
        if (!e.Frame.IsMain || _browser is null)
            return;

        // Only sync for the in-process app origin (not external navigations).
        if (!IsAppOrigin(e.Url))
            return;

        const string script = """
            (function () {
              var t = null;
              try {
                var g = window.dysonTheme && window.dysonTheme.get && window.dysonTheme.get();
                if (g && g.theme) t = g.theme;
              } catch (e) {}
              if (!t)
                t = document.documentElement.getAttribute('data-theme');
              if (window.dysonShell && window.dysonShell.notifyTheme)
                window.dysonShell.notifyTheme(t || 'dark');
            })();
            """;

        _ = e.Frame.EvaluateScriptAsync(script);
    }

    private bool IsAppOrigin(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return string.Equals(uri.Scheme, _appUrl.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, _appUrl.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == _appUrl.Port;
    }
}
