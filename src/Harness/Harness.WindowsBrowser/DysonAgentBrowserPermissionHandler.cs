using CefSharp;
using CefSharp.Handler;

namespace DysonHarness;

/// <summary>
/// Agent-browser only: accept every permission prompt and grant requested media
/// capture with no UI. Not installed on the desktop shell webview.
/// </summary>
internal class DysonAgentBrowserPermissionHandler : PermissionHandler
{
    /// <summary>
    /// Any <see cref="PermissionRequestType"/> combination is an explicit accept.
    /// CefSharp's prompt callback grants the requested set; it does not take a flag mask.
    /// </summary>
    internal static PermissionRequestResult GrantPrompt(PermissionRequestType requested) =>
        PermissionRequestResult.Accept;

    /// <summary>
    /// Grant exactly the bits the page asked for (device and/or desktop audio/video).
    /// CEF requires an exact match for getUserMedia; a superset fails the request.
    /// </summary>
    internal static MediaAccessPermissionType GrantMedia(MediaAccessPermissionType requested) =>
        requested;

    protected override bool OnShowPermissionPrompt(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        ulong promptId,
        string requestingOrigin,
        PermissionRequestType requestedPermissions,
        IPermissionPromptCallback callback)
    {
        using (callback)
        {
            callback.Continue(GrantPrompt(requestedPermissions));
            return true;
        }
    }

    protected override bool OnRequestMediaAccessPermission(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        string requestingOrigin,
        MediaAccessPermissionType requestedPermissions,
        IMediaAccessCallback callback)
    {
        using (callback)
        {
            callback.Continue(GrantMedia(requestedPermissions));
            return true;
        }
    }

    protected override void OnDismissPermissionPrompt(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        ulong promptId,
        PermissionRequestResult result)
    {
        // Accept already ran in OnShowPermissionPrompt. A later dismiss
        // (the accept itself, navigation, or close) must not become a deny.
    }
}
