using System.Diagnostics;
using CefSharp;
using CefSharp.Handler;

namespace DysonHarness.UI.Windows;

/// <summary>
/// Keep the shell on the Blazor loopback origin; open other http(s) navigations/popups in the OS browser.
/// </summary>
internal sealed class ExternalNavigationHandlers : RequestHandler, ILifeSpanHandler
{
    private readonly Uri _appOrigin;

    public ExternalNavigationHandlers(Uri appOrigin)
    {
        _appOrigin = appOrigin ?? throw new ArgumentNullException(nameof(appOrigin));
    }

    protected override bool OnBeforeBrowse(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        IRequest request,
        bool userGesture,
        bool isRedirect)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            return false;

        if (IsAppOrigin(uri) || IsBrowserInternal(uri))
            return false;

        if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        {
            OpenInOsBrowser(uri.AbsoluteUri);
            return true;
        }

        return false;
    }

    public bool OnBeforePopup(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        string targetUrl,
        string targetFrameName,
        WindowOpenDisposition targetDisposition,
        bool userGesture,
        IPopupFeatures popupFeatures,
        IWindowInfo windowInfo,
        IBrowserSettings browserSettings,
        ref bool noJavascriptAccess,
        out IWebBrowser? newBrowser)
    {
        newBrowser = null;

        if (Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !IsAppOrigin(uri))
        {
            OpenInOsBrowser(uri.AbsoluteUri);
        }

        return true;
    }

    public void OnAfterCreated(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
    }

    public bool DoClose(IWebBrowser chromiumWebBrowser, IBrowser browser) => false;

    public void OnBeforeClose(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
    }

    private bool IsAppOrigin(Uri uri) =>
        string.Equals(uri.Scheme, _appOrigin.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, _appOrigin.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == _appOrigin.Port;

    private static bool IsBrowserInternal(Uri uri) =>
        uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase)
        || uri.Scheme.Equals("chrome", StringComparison.OrdinalIgnoreCase)
        || uri.Scheme.Equals("devtools", StringComparison.OrdinalIgnoreCase)
        || uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase);

    private static void OpenInOsBrowser(string absoluteUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = absoluteUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Ignore OS-open failures; navigation was already cancelled.
        }
    }
}
