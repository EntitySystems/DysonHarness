using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DysonHarness.UI.Windows;

/// <summary>
/// Desktop shell: HWND-hosted WebView2 (profile <c>shell</c>) on the in-process Blazor loopback origin.
/// Off-origin http(s) navigations and popups open in the OS browser; only the microphone permission is granted.
/// </summary>
public partial class MainWindow : Window
{
    private const string ThemeSyncScript = """
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

    private readonly Uri _appUrl;
    private readonly WebView2 _browser;
    private string _chromeTheme = "dark";

    public MainWindow(Uri appUrl)
    {
        _appUrl = appUrl ?? throw new ArgumentNullException(nameof(appUrl));
        InitializeComponent();

        SourceInitialized += (_, _) => WindowChromeTheme.Apply(this, _chromeTheme);

        // Dark background avoids a white flash before the first Blazor paint.
        _browser = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 0x0c, 0x0e, 0x11) };
        Root.Children.Add(_browser);
        // Default dark until the page reports (matches ThemeService default).
        WindowChromeTheme.Apply(this, _chromeTheme);
        // The WPF WebView2 control re-raises browser accelerator keys (F5) as WPF key events.
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += async (_, _) => await InitializeBrowserAsync().ConfigureAwait(true);
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var env = await DysonWindowsStaHost.GetEnvironmentAsync().ConfigureAwait(true);
            if (env.IsError)
            {
                ShowInitError(env.Error);
                return;
            }

            var options = env.Value.CreateCoreWebView2ControllerOptions();
            options.ProfileName = DysonWebView2Interop.ShellProfile;
            await _browser.EnsureCoreWebView2Async(env.Value, options).ConfigureAwait(true);
            var core = _browser.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;

            // theme.js calls window.dysonShell.notifyTheme(t); the shim posts it here.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(DysonWebView2Interop.ShellBridgeScript).ConfigureAwait(true);
            core.WebMessageReceived += (_, e) =>
            {
                if (IsAppOrigin(e.Source) && DysonWebView2Interop.ParseThemeMessage(e.WebMessageAsJson) is { } theme)
                    ApplyChromeTheme(theme);
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess && IsAppOrigin(core.Source))
                    _ = core.ExecuteScriptAsync(ThemeSyncScript);
            };
            core.NavigationStarting += (_, e) =>
            {
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
                    && DysonWebView2Interop.ShouldOpenExternally(uri, _appUrl))
                {
                    e.Cancel = true;
                    OpenInOsBrowser(uri.AbsoluteUri);
                }
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
                    && DysonWebView2Interop.ShouldOpenExternally(uri, _appUrl))
                {
                    OpenInOsBrowser(uri.AbsoluteUri);
                }
            };
            // Composer push-to-talk: microphone only.
            core.PermissionRequested += (_, e) =>
                e.State = DysonWebView2Interop.DecidePermission(e.PermissionKind, isShell: true);
            core.ContextMenuRequested += (_, e) =>
            {
                var reload = env.Value.CreateContextMenuItem("Reload page", null, CoreWebView2ContextMenuItemKind.Command);
                reload.CustomItemSelected += (_, _) => core.Reload();
                e.MenuItems.Insert(0, reload);
                e.MenuItems.Insert(1, env.Value.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));
            };

            core.Navigate(_appUrl.AbsoluteUri);
        }
        catch (Exception ex)
        {
            ShowInitError(ex.Message);
        }
    }

    private void ShowInitError(string message) =>
        MessageBox.Show(this, "The WebView2 shell failed to start:" + Environment.NewLine + message,
            "DysonHarness", MessageBoxButton.OK, MessageBoxImage.Error);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F5 || e.IsRepeat || _browser.CoreWebView2 is not { } core)
            return;

        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            _ = core.CallDevToolsProtocolMethodAsync("Page.reload", """{"ignoreCache":true}""");
        else
            core.Reload();
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

    private bool IsAppOrigin(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && DysonWebView2Interop.IsSameOrigin(uri, _appUrl);

    private static void OpenInOsBrowser(string absoluteUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = absoluteUrl, UseShellExecute = true });
        }
        catch
        {
            // Ignore OS-open failures; navigation was already cancelled.
        }
    }
}
