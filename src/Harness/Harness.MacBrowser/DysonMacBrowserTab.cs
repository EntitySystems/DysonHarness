using System.Text.Json;

namespace DysonHarness;

internal sealed class DysonMacBrowserTab : IDysonBrowserTab
{
    private readonly Func<string, string> _invoke;

    public DysonMacBrowserTab(Func<string, string> invoke, string windowId, string id)
    {
        _invoke = invoke;
        WindowId = windowId;
        Id = id;
    }

    public string Id { get; }
    public string WindowId { get; }

    public Task<Result<string, string>> GetUrlAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Text(DysonMacWebKitProtocol.GetUrl(WindowId, Id)), cancellationToken);

    public Task<Result<string, string>> GetTitleAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Text(DysonMacWebKitProtocol.GetTitle(WindowId, Id)), cancellationToken);

    public Task<VoidResult<string>> NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Task.FromResult(VoidResult<string>.AsError("url is required"));
        return Task.Run(() => Void(DysonMacWebKitProtocol.Navigate(WindowId, Id, url)), cancellationToken);
    }

    public Task<VoidResult<string>> ReloadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Void(DysonMacWebKitProtocol.Reload(WindowId, Id, ignoreCache: false)), cancellationToken);

    public Task<VoidResult<string>> GoBackAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Void(DysonMacWebKitProtocol.GoBack(WindowId, Id)), cancellationToken);

    public Task<VoidResult<string>> GoForwardAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Void(DysonMacWebKitProtocol.GoForward(WindowId, Id)), cancellationToken);

    public async Task<VoidResult<string>> ClickAsync(
        DysonBrowserClickRequest request,
        CancellationToken cancellationToken = default)
    {
        var script = DysonBrowserDomScripts.Click(request);
        if (script.IsError)
            return VoidResult<string>.AsError(script.Error);
        return DysonBrowserDomScripts.InterpretOk(await ExecuteJavaScriptAsync(script.Value, cancellationToken).ConfigureAwait(false));
    }

    public async Task<VoidResult<string>> TypeAsync(
        DysonBrowserTypeRequest request,
        CancellationToken cancellationToken = default)
    {
        var script = DysonBrowserDomScripts.Type(request);
        if (script.IsError)
            return VoidResult<string>.AsError(script.Error);
        return DysonBrowserDomScripts.InterpretOk(await ExecuteJavaScriptAsync(script.Value, cancellationToken).ConfigureAwait(false));
    }

    public async Task<VoidResult<string>> FillAsync(
        string selector,
        string value,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return VoidResult<string>.AsError("selector is required");
        return await TypeAsync(new DysonBrowserTypeRequest
        {
            Selector = selector,
            Text = value ?? "",
            ClearFirst = true,
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VoidResult<string>> HoverAsync(string selector, CancellationToken cancellationToken = default)
    {
        var script = DysonBrowserDomScripts.Hover(selector);
        if (script.IsError)
            return VoidResult<string>.AsError(script.Error);
        return DysonBrowserDomScripts.InterpretOk(await ExecuteJavaScriptAsync(script.Value, cancellationToken).ConfigureAwait(false));
    }

    public async Task<VoidResult<string>> PressKeyAsync(
        DysonBrowserKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        var script = DysonBrowserDomScripts.PressKey(request);
        if (script.IsError)
            return VoidResult<string>.AsError(script.Error);
        return DysonBrowserDomScripts.InterpretOk(await ExecuteJavaScriptAsync(script.Value, cancellationToken).ConfigureAwait(false));
    }

    public async Task<VoidResult<string>> WaitForSelectorAsync(
        string selector,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return VoidResult<string>.AsError("selector is required");

        var timeout = timeoutMs is > 0 ? timeoutMs.Value : DysonBrowserDefaults.DefaultTimeoutMs;
        var probe = DysonBrowserDomScripts.SelectorProbe(JsonSerializer.Serialize(selector));
        var deadline = Environment.TickCount64 + timeout;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ExecuteJavaScriptAsync(probe, cancellationToken).ConfigureAwait(false);
            if (result.IsError)
                return VoidResult<string>.AsError(result.Error);
            if (string.Equals(result.Value, "ok", StringComparison.Ordinal))
                return VoidResult<string>.Success;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return VoidResult<string>.AsError($"Timeout waiting for selector: {selector}");
    }

    public Task<VoidResult<string>> WaitForNavigationAsync(
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        var timeout = timeoutMs is > 0 ? timeoutMs.Value : DysonBrowserDefaults.DefaultTimeoutMs;
        return Task.Run(() => Void(DysonMacWebKitProtocol.WaitNav(WindowId, Id, timeout)), cancellationToken);
    }

    public async Task<Result<string, string>> ExecuteJavaScriptAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Result<string, string>.AsError("code is required");

        var evalTask = Task.Run(
            () => _invoke(DysonMacWebKitProtocol.Eval(WindowId, Id, code, DysonBrowserDefaults.DefaultTimeoutMs)));
        var cancelTask = Task.Delay(Timeout.Infinite, cancellationToken);
        var winner = await Task.WhenAny(evalTask, cancelTask).ConfigureAwait(false);
        if (winner != evalTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result<string, string>.AsError("JavaScript evaluation timed out.");
        }

        var parsed = DysonMacWebKitProtocol.Parse(await evalTask.ConfigureAwait(false));
        if (!parsed.Ok)
            return Result<string, string>.AsError(parsed.Error ?? "JavaScript evaluation failed.");
        return Result<string, string>.AsValue(parsed.Value ?? "");
    }

    public Task<Result<string, string>> GetHtmlAsync(CancellationToken cancellationToken = default) =>
        ExecuteJavaScriptAsync(DysonBrowserDomScripts.OuterHtml, cancellationToken);

    public async Task<Result<byte[], string>> TakeScreenshotAsync(
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        var timeout = timeoutMs is > 0 ? timeoutMs.Value : DysonBrowserDefaults.DefaultTimeoutMs;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        var shotTask = Task.Run(() => _invoke(DysonMacWebKitProtocol.Screenshot(WindowId, Id, timeout)));
        var cancelTask = Task.Delay(Timeout.Infinite, linked.Token);
        try
        {
            var winner = await Task.WhenAny(shotTask, cancelTask).ConfigureAwait(false);
            if (winner != shotTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Result<byte[], string>.AsError($"Screenshot timed out after {timeout}ms.");
            }

            var parsed = DysonMacWebKitProtocol.Parse(await shotTask.ConfigureAwait(false));
            if (!parsed.Ok)
                return Result<byte[], string>.AsError(parsed.Error ?? "Screenshot capture returned empty.");
            if (parsed.Png is not { Length: > 0 })
                return Result<byte[], string>.AsError("Screenshot capture returned empty.");
            return Result<byte[], string>.AsValue(parsed.Png);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<byte[], string>.AsError($"Screenshot timed out after {timeout}ms.");
        }
    }

    public Task<Result<IReadOnlyList<DysonBrowserConsoleEntry>, string>> ReadConsoleLogAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var parsed = Call(DysonMacWebKitProtocol.ReadConsole(WindowId, Id));
            return parsed.Ok
                ? Result<IReadOnlyList<DysonBrowserConsoleEntry>, string>.AsValue(parsed.Console)
                : Result<IReadOnlyList<DysonBrowserConsoleEntry>, string>.AsError(parsed.Error ?? "ReadConsoleLog failed.");
        }, cancellationToken);

    public Task<Result<IReadOnlyList<DysonBrowserNetworkEntry>, string>> ReadNetworkLogAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var parsed = Call(DysonMacWebKitProtocol.ReadNetwork(WindowId, Id));
            return parsed.Ok
                ? Result<IReadOnlyList<DysonBrowserNetworkEntry>, string>.AsValue(parsed.Network)
                : Result<IReadOnlyList<DysonBrowserNetworkEntry>, string>.AsError(parsed.Error ?? "ReadNetworkLog failed.");
        }, cancellationToken);

    public Task<VoidResult<string>> ClearConsoleLogAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Void(DysonMacWebKitProtocol.ClearConsole(WindowId, Id)), cancellationToken);

    public Task<VoidResult<string>> ClearNetworkLogAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Void(DysonMacWebKitProtocol.ClearNetwork(WindowId, Id)), cancellationToken);

    private DysonMacWebKitResponse Call(string request) =>
        DysonMacWebKitProtocol.Parse(_invoke(request));

    private Result<string, string> Text(string request)
    {
        var parsed = Call(request);
        return parsed.Ok
            ? Result<string, string>.AsValue(parsed.Value ?? "")
            : Result<string, string>.AsError(parsed.Error ?? "browser call failed");
    }

    private VoidResult<string> Void(string request)
    {
        var parsed = Call(request);
        return parsed.Ok
            ? VoidResult<string>.Success
            : VoidResult<string>.AsError(parsed.Error ?? "browser call failed");
    }
}
