namespace DysonHarness;

/// <summary>One top-level agent browser window (WPF chrome + CEF content).</summary>
public interface IDysonBrowserWindow
{
    string Id { get; }

    Task<Result<IReadOnlyList<IDysonBrowserTab>, string>> ListTabsAsync(
        CancellationToken cancellationToken = default);

    Task<Result<IDysonBrowserTab, string>> NewTabAsync(
        string? url = null,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> CloseTabAsync(
        string tabId,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> ActivateTabAsync(
        string tabId,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> CloseAsync(CancellationToken cancellationToken = default);

    Task<VoidResult<string>> ResizeAsync(
        int width,
        int height,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> BringToFrontAsync(CancellationToken cancellationToken = default);
}
