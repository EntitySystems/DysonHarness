using CefSharp;
using CefSharp.Handler;

namespace DysonHarness;

/// <summary>
/// Desktop shell webview only (<c>MainWindow</c>): grants microphone capture for composer push-to-talk
/// and nothing else. Video, desktop capture, and combined requests are denied. Agent browser windows keep
/// <see cref="DysonAgentBrowserPermissionHandler"/>.
/// </summary>
public class DysonShellMicPermission : PermissionHandler
{
    /// <summary><see cref="MediaAccessPermissionType.AudioCapture"/> alone ⇒ granted; anything else ⇒ null (cancel).</summary>
    internal static MediaAccessPermissionType? GrantMedia(MediaAccessPermissionType requested) =>
        requested == MediaAccessPermissionType.AudioCapture ? MediaAccessPermissionType.AudioCapture : null;

    /// <summary>Only a mic-only prompt is accepted.</summary>
    internal static PermissionRequestResult GrantPrompt(PermissionRequestType requested) =>
        requested == PermissionRequestType.MicStream ? PermissionRequestResult.Accept : PermissionRequestResult.Deny;

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
            if (GrantMedia(requestedPermissions) is { } granted)
                callback.Continue(granted);
            else
                callback.Cancel();
            return true;
        }
    }
}
