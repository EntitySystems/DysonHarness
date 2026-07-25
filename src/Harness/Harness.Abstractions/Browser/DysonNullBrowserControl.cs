namespace DysonHarness;

/// <summary>
/// No-op browser control for non-Windows hosts and tests.
/// All methods return <c>browser control unavailable</c>.
/// </summary>
public sealed class DysonNullBrowserControl : IDysonBrowserControl
{
    public static DysonNullBrowserControl Instance { get; } = new();

    private const string Unavailable = "browser control unavailable";

    public Task<Result<IDysonBrowserWindow, string>> OpenBrowserAsync(
        string? url = null,
        int? width = null,
        int? height = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<IDysonBrowserWindow, string>.AsError(Unavailable));

    public Task<Result<IReadOnlyList<IDysonBrowserWindow>, string>> ListWindowsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<IReadOnlyList<IDysonBrowserWindow>, string>.AsError(Unavailable));

    public Task<Result<IDysonBrowserWindow, string>> GetWindowAsync(
        string windowId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<IDysonBrowserWindow, string>.AsError(Unavailable));
}
