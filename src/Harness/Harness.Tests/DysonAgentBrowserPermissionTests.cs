#if WINDOWS
using CefSharp;
using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Agent browser grants every CefSharp permission prompt and the requested media bits.
/// Does not launch CEF.
/// </summary>
public class DysonAgentBrowserPermissionTests
{
    [Fact]
    public void Grants_every_prompt_type_and_requested_media_capture()
    {
        var handler = new Probe();

        var allPrompt = PermissionRequestType.None;
        foreach (PermissionRequestType value in Enum.GetValues<PermissionRequestType>())
            allPrompt |= value;

        Assert.True(allPrompt.HasFlag(PermissionRequestType.Geolocation));
        Assert.True(allPrompt.HasFlag(PermissionRequestType.Clipboard));
        Assert.True(allPrompt.HasFlag(PermissionRequestType.Notifications));
        Assert.True(allPrompt.HasFlag(PermissionRequestType.CameraStream));
        Assert.True(allPrompt.HasFlag(PermissionRequestType.MicStream));
        Assert.True(allPrompt.HasFlag(PermissionRequestType.MidiSysex));
        Assert.True(allPrompt.HasFlag(PermissionRequestType.StorageAccess));
        Assert.True(allPrompt.HasFlag(PermissionRequestType.ProtectedMediaIdentifier));
        Assert.Equal(PermissionRequestResult.Accept, DysonAgentBrowserPermissionHandler.GrantPrompt(allPrompt));

        var prompt = new PromptCallback();
        Assert.True(handler.Prompt(allPrompt, prompt));
        Assert.Equal(PermissionRequestResult.Accept, prompt.Result);
        Assert.True(prompt.Disposed);

        var allMedia = MediaAccessPermissionType.AudioCapture
            | MediaAccessPermissionType.VideoCapture
            | MediaAccessPermissionType.DesktopAudioCapture
            | MediaAccessPermissionType.DesktopVideoCapture;
        Assert.Equal(allMedia, DysonAgentBrowserPermissionHandler.GrantMedia(allMedia));

        var media = new MediaCallback();
        Assert.True(handler.Media(allMedia, media));
        Assert.Equal(allMedia, media.Allowed);
        Assert.False(media.Cancelled);
        Assert.True(media.Disposed);

        handler.Dismiss(PermissionRequestResult.Ignore);
    }

    private sealed class Probe : DysonAgentBrowserPermissionHandler
    {
        public bool Prompt(PermissionRequestType requested, IPermissionPromptCallback callback) =>
            OnShowPermissionPrompt(null!, null!, 1, "https://example.test", requested, callback);

        public bool Media(MediaAccessPermissionType requested, IMediaAccessCallback callback) =>
            OnRequestMediaAccessPermission(
                null!, null!, null!, "https://example.test", requested, callback);

        public void Dismiss(PermissionRequestResult result) =>
            OnDismissPermissionPrompt(null!, null!, 1, result);
    }

    private sealed class PromptCallback : IPermissionPromptCallback
    {
        public PermissionRequestResult? Result { get; private set; }
        public bool Disposed { get; private set; }

        public void Continue(PermissionRequestResult result) => Result = result;

        public void Dispose() => Disposed = true;
    }

    private sealed class MediaCallback : IMediaAccessCallback
    {
        public MediaAccessPermissionType? Allowed { get; private set; }
        public bool Cancelled { get; private set; }
        public bool Disposed { get; private set; }

        public void Continue(MediaAccessPermissionType allowedPermissions) => Allowed = allowedPermissions;

        public void Cancel() => Cancelled = true;

        public void Dispose() => Disposed = true;
    }
}
#else
namespace Harness.Tests;

/// <summary>Agent browser permission grant is Windows / CefSharp only.</summary>
public class DysonAgentBrowserPermissionTests
{
    [Fact(Skip = "Windows CefSharp only")]
    public void Grants_every_prompt_type_and_requested_media_capture()
    {
    }
}
#endif
