namespace DysonHarness;

/// <summary>
/// Process-wide browser control (Windows CefSharp host today; null/unavailable elsewhere).
/// Register as a DI singleton from the UI host.
/// </summary>
public interface IDysonBrowserControl
{
    /// <summary>
    /// Raised when the user completes a Snip in a browser window (cropped JPEG/PNG bytes).
    /// UI host subscribes; WindowsBrowser must not call the host directly.
    /// </summary>
    event Action<DysonBrowserSnipPayload>? SnipCaptured;

    Task<Result<IDysonBrowserWindow, string>> OpenBrowserAsync(
        string? url = null,
        int? width = null,
        int? height = null,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<IDysonBrowserWindow>, string>> ListWindowsAsync(
        CancellationToken cancellationToken = default);

    Task<Result<IDysonBrowserWindow, string>> GetWindowAsync(
        string windowId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear the shared CEF HTTP cache once, then hard-reload every tab in every open agent window.
    /// Does not clear cookies or site storage. Empty window list is success with zeros.
    /// </summary>
    Task<Result<DysonBrowserCacheClearResult, string>> ClearBrowserCacheAsync(
        CancellationToken cancellationToken = default);
}
