namespace DysonHarness;

public sealed class DysonMacBrowserControl : IDysonBrowserControl
{
    private readonly Func<string, string> _invoke;

    public DysonMacBrowserControl()
        : this(DysonMacWebKitNative.Invoke)
    {
    }

    internal DysonMacBrowserControl(Func<string, string> invoke)
    {
        _invoke = invoke;
    }

    public event Action<DysonBrowserSnipPayload>? SnipCaptured;

    internal void RaiseSnip(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("imageBase64", out var b64) || b64.GetString() is not { Length: > 0 } text)
                return;

            var bytes = Convert.FromBase64String(text);
            SnipCaptured?.Invoke(new DysonBrowserSnipPayload
            {
                ImageBytes = bytes,
                FileName = SnipString(root, "fileName") ?? "browser-snip.jpg",
                HtmlRef = null,
                Url = SnipString(root, "url"),
                ScrollY = SnipDouble(root, "scrollY"),
                ScrollHeight = SnipDouble(root, "scrollHeight"),
                ViewportHeight = SnipDouble(root, "viewportHeight"),
                PercentDown = SnipInt(root, "percentDown"),
            });
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            // A bad snip payload must not take down the browser window.
        }
    }

    private static string? SnipString(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String
            ? el.GetString()
            : null;

    private static double? SnipDouble(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.TryGetDouble(out var n) ? n : null;

    private static int? SnipInt(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.TryGetInt32(out var n) ? n : null;

    public Task<Result<IDysonBrowserWindow, string>> OpenBrowserAsync(
        string? url = null,
        int? width = null,
        int? height = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var w = width is > 0 ? width.Value : 1280;
            var h = height is > 0 ? height.Value : 800;
            var parsed = Call(DysonMacWebKitProtocol.OpenWindow(url, w, h));
            if (!parsed.Ok || string.IsNullOrEmpty(parsed.WindowId))
                return Result<IDysonBrowserWindow, string>.AsError(parsed.Error ?? "OpenBrowser failed.");
            IDysonBrowserWindow window = new DysonMacBrowserWindow(_invoke, parsed.WindowId);
            return Result<IDysonBrowserWindow, string>.AsValue(window);
        }, cancellationToken);

    public Task<Result<IReadOnlyList<IDysonBrowserWindow>, string>> ListWindowsAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var parsed = Call(DysonMacWebKitProtocol.ListWindows());
            if (!parsed.Ok)
                return Result<IReadOnlyList<IDysonBrowserWindow>, string>.AsError(parsed.Error ?? "ListBrowserWindows failed.");
            IReadOnlyList<IDysonBrowserWindow> windows = parsed.Windows
                .Select(row => (IDysonBrowserWindow)new DysonMacBrowserWindow(_invoke, row.Id))
                .ToArray();
            return Result<IReadOnlyList<IDysonBrowserWindow>, string>.AsValue(windows);
        }, cancellationToken);

    public Task<Result<IDysonBrowserWindow, string>> GetWindowAsync(
        string windowId,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var parsed = Call(DysonMacWebKitProtocol.GetWindow(windowId));
            if (!parsed.Ok || string.IsNullOrEmpty(parsed.WindowId))
                return Result<IDysonBrowserWindow, string>.AsError(parsed.Error ?? $"Window not found: {windowId}");
            IDysonBrowserWindow window = new DysonMacBrowserWindow(_invoke, parsed.WindowId);
            return Result<IDysonBrowserWindow, string>.AsValue(window);
        }, cancellationToken);

    public Task<Result<DysonBrowserCacheClearResult, string>> ClearBrowserCacheAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var parsed = Call(DysonMacWebKitProtocol.ClearCache());
            if (!parsed.Ok)
                return Result<DysonBrowserCacheClearResult, string>.AsError(parsed.Error ?? "ClearBrowserCache failed.");
            return Result<DysonBrowserCacheClearResult, string>.AsValue(new DysonBrowserCacheClearResult
            {
                Windows = parsed.WindowsCleared,
                TabsReloaded = parsed.TabsReloaded,
            });
        }, cancellationToken);

    private DysonMacWebKitResponse Call(string request) =>
        DysonMacWebKitProtocol.Parse(_invoke(request));
}
