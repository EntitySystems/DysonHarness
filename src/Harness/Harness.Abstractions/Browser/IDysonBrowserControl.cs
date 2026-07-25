namespace DysonHarness;

/// <summary>
/// Process-wide browser control (Windows CefSharp host today; null/unavailable elsewhere).
/// Register as a DI singleton from the UI host.
/// </summary>
public interface IDysonBrowserControl
{
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
}
