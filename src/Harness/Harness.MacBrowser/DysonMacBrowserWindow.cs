namespace DysonHarness;

internal sealed class DysonMacBrowserWindow : IDysonBrowserWindow
{
    private readonly Func<string, string> _invoke;

    public DysonMacBrowserWindow(Func<string, string> invoke, string id)
    {
        _invoke = invoke;
        Id = id;
    }

    public string Id { get; }

    public Task<Result<IReadOnlyList<IDysonBrowserTab>, string>> ListTabsAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var parsed = Call(DysonMacWebKitProtocol.ListTabs(Id));
            if (!parsed.Ok)
                return Result<IReadOnlyList<IDysonBrowserTab>, string>.AsError(parsed.Error ?? "ListBrowserTabs failed.");
            IReadOnlyList<IDysonBrowserTab> tabs = parsed.Tabs
                .Select(row => (IDysonBrowserTab)new DysonMacBrowserTab(_invoke, Id, row.Id))
                .ToArray();
            return Result<IReadOnlyList<IDysonBrowserTab>, string>.AsValue(tabs);
        }, cancellationToken);

    public Task<Result<IDysonBrowserTab, string>> NewTabAsync(
        string? url = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var parsed = Call(DysonMacWebKitProtocol.NewTab(Id, url));
            if (!parsed.Ok || string.IsNullOrEmpty(parsed.TabId))
                return Result<IDysonBrowserTab, string>.AsError(parsed.Error ?? "NewBrowserTab failed.");
            IDysonBrowserTab tab = new DysonMacBrowserTab(_invoke, Id, parsed.TabId);
            return Result<IDysonBrowserTab, string>.AsValue(tab);
        }, cancellationToken);

    public Task<VoidResult<string>> CloseTabAsync(string tabId, CancellationToken cancellationToken = default) =>
        Task.Run(() => Void(DysonMacWebKitProtocol.CloseTab(Id, tabId)), cancellationToken);

    public Task<VoidResult<string>> ActivateTabAsync(string tabId, CancellationToken cancellationToken = default) =>
        Task.Run(() => Void(DysonMacWebKitProtocol.ActivateTab(Id, tabId)), cancellationToken);

    public Task<VoidResult<string>> CloseAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Void(DysonMacWebKitProtocol.CloseWindow(Id)), cancellationToken);

    public Task<VoidResult<string>> ResizeAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        if (width < 200 || height < 200)
            return Task.FromResult(VoidResult<string>.AsError("Width and height must be at least 200."));
        return Task.Run(() => Void(DysonMacWebKitProtocol.ResizeWindow(Id, width, height)), cancellationToken);
    }

    public Task<VoidResult<string>> BringToFrontAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Void(DysonMacWebKitProtocol.BringToFront(Id)), cancellationToken);

    private DysonMacWebKitResponse Call(string request) =>
        DysonMacWebKitProtocol.Parse(_invoke(request));

    private VoidResult<string> Void(string request)
    {
        var parsed = Call(request);
        return parsed.Ok
            ? VoidResult<string>.Success
            : VoidResult<string>.AsError(parsed.Error ?? "browser call failed");
    }
}
