namespace DysonHarness;

/// <summary>One tab inside an <see cref="IDysonBrowserWindow"/>.</summary>
public interface IDysonBrowserTab
{
    string Id { get; }
    string WindowId { get; }

    Task<Result<string, string>> GetUrlAsync(CancellationToken cancellationToken = default);
    Task<Result<string, string>> GetTitleAsync(CancellationToken cancellationToken = default);

    Task<VoidResult<string>> NavigateAsync(string url, CancellationToken cancellationToken = default);
    Task<VoidResult<string>> ReloadAsync(CancellationToken cancellationToken = default);
    Task<VoidResult<string>> GoBackAsync(CancellationToken cancellationToken = default);
    Task<VoidResult<string>> GoForwardAsync(CancellationToken cancellationToken = default);

    Task<VoidResult<string>> ClickAsync(
        DysonBrowserClickRequest request,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> TypeAsync(
        DysonBrowserTypeRequest request,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> FillAsync(
        string selector,
        string value,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> HoverAsync(
        string selector,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> PressKeyAsync(
        DysonBrowserKeyRequest request,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> WaitForSelectorAsync(
        string selector,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> WaitForNavigationAsync(
        int? timeoutMs = null,
        CancellationToken cancellationToken = default);

    Task<Result<string, string>> ExecuteJavaScriptAsync(
        string code,
        CancellationToken cancellationToken = default);

    Task<Result<string, string>> GetHtmlAsync(CancellationToken cancellationToken = default);

    Task<Result<byte[], string>> TakeScreenshotAsync(CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DysonBrowserConsoleEntry>, string>> ReadConsoleLogAsync(
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DysonBrowserNetworkEntry>, string>> ReadNetworkLogAsync(
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> ClearConsoleLogAsync(CancellationToken cancellationToken = default);
    Task<VoidResult<string>> ClearNetworkLogAsync(CancellationToken cancellationToken = default);
}
