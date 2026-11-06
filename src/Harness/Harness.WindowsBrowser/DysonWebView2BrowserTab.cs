using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DysonHarness;

/// <summary>
/// Agent browser tab on the HWND-hosted WPF <see cref="WebView2"/> (profile <c>agent</c>). Every CoreWebView2 call
/// runs on the <see cref="DysonWindowsStaHost"/> dispatcher after <see cref="_init"/> completes.
/// </summary>
internal sealed class DysonWebView2BrowserTab : IDysonBrowserTab
{
    private readonly DysonWindowsBrowserWindow _window;
    private readonly ConcurrentQueue<DysonBrowserConsoleEntry> _console = new();
    private readonly ConcurrentQueue<DysonBrowserNetworkEntry> _network = new();
    private readonly object _navGate = new();
    private readonly Task<VoidResult<string>> _init;
    private TaskCompletionSource? _navTcs;

    /// <summary>Must be constructed on the STA dispatcher thread.</summary>
    public DysonWebView2BrowserTab(DysonWindowsBrowserWindow window, string? initialUrl)
    {
        _window = window;
        Id = Guid.NewGuid().ToString("N");
        WindowId = window.Id;

        // Windowed (HwndHost) so WebGPU/WebGL present into a real HWND. Snip uses a CDP screenshot overlay
        // because HWND airspace blocks live WPF rubber-bands.
        BrowserControl = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 0x0c, 0x0e, 0x11),
        };
        _init = InitializeAsync(string.IsNullOrWhiteSpace(initialUrl) ? "about:blank" : initialUrl);
    }

    public string Id { get; }
    public string WindowId { get; }
    public WebView2 BrowserControl { get; }
    public string? CurrentAddress { get; private set; }
    public string? CurrentTitle { get; private set; }

    /// <summary>Agent profile of this tab once initialized (ClearBrowserCache target).</summary>
    internal CoreWebView2Profile? Profile => BrowserControl.CoreWebView2?.Profile;

    private async Task<VoidResult<string>> InitializeAsync(string url)
    {
        try
        {
            var env = await DysonWindowsStaHost.GetEnvironmentAsync().ConfigureAwait(true);
            if (env.IsError)
                return VoidResult<string>.AsError(env.Error);

            var options = env.Value.CreateCoreWebView2ControllerOptions();
            options.ProfileName = DysonWebView2Interop.AgentProfile;
            // Completes once the control has an HWND (the window keeps every tab in its visual tree).
            await BrowserControl.EnsureCoreWebView2Async(env.Value, options).ConfigureAwait(true);
            var core = BrowserControl.CoreWebView2;

            core.SourceChanged += (_, _) =>
            {
                CurrentAddress = core.Source;
                _window.SyncAddress(Id, CurrentAddress, CurrentTitle);
            };
            core.DocumentTitleChanged += (_, _) =>
            {
                CurrentTitle = core.DocumentTitle;
                _window.SyncAddress(Id, CurrentAddress, CurrentTitle);
            };
            core.NavigationCompleted += (_, e) =>
            {
                _network.Enqueue(new DysonBrowserNetworkEntry
                {
                    Url = core.Source ?? CurrentAddress ?? "",
                    Method = "GET",
                    Status = e.HttpStatusCode > 0 ? e.HttpStatusCode : null,
                    Timestamp = DateTimeOffset.UtcNow,
                });
                lock (_navGate)
                {
                    _navTcs?.TrySetResult();
                    _navTcs = null;
                }
            };
            core.PermissionRequested += (_, e) =>
                e.State = DysonWebView2Interop.DecidePermission(e.PermissionKind, isShell: false);
            // window.open / target=_blank → new tab in this window (matches macOS).
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                _ = _window.NewTabAsync(e.Uri);
            };

            foreach (var name in new[] { "Runtime.consoleAPICalled", "Runtime.exceptionThrown" })
            {
                core.GetDevToolsProtocolEventReceiver(name).DevToolsProtocolEventReceived += (_, e) =>
                {
                    if (DysonWebView2Interop.ParseConsoleEvent(name, e.ParameterObjectAsJson, DateTimeOffset.UtcNow) is { } entry)
                        _console.Enqueue(entry);
                };
            }

            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}").ConfigureAwait(true);
            core.Navigate(url);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError("WebView2 tab failed to initialize: " + ex.Message, ex);
        }
    }

    private Task<Result<T, string>> WithCoreAsync<T>(Func<CoreWebView2, Task<Result<T, string>>> action) =>
        DysonWindowsStaHost.InvokeAsync(async () =>
        {
            var ready = await _init.ConfigureAwait(true);
            if (ready.IsError)
                return Result<T, string>.AsError(ready.Error);
            try
            {
                return await action(BrowserControl.CoreWebView2).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                return Result<T, string>.AsError(ex.Message, ex);
            }
        });

    private async Task<VoidResult<string>> WithCoreAsync(Action<CoreWebView2> action)
    {
        var result = await WithCoreAsync(core =>
        {
            action(core);
            return Task.FromResult(Result<bool, string>.AsValue(true));
        }).ConfigureAwait(false);
        return result.IsError ? VoidResult<string>.AsError(result.Error) : VoidResult<string>.Success;
    }

    public void DisposeBrowser()
    {
        try
        {
            BrowserControl.Dispose();
        }
        catch
        {
            // best-effort on close
        }
    }

    public Task<Result<string, string>> GetUrlAsync(CancellationToken cancellationToken = default) =>
        DysonWindowsStaHost.InvokeAsync(() =>
            Result<string, string>.AsValue(CurrentAddress ?? BrowserControl.CoreWebView2?.Source ?? ""));

    public Task<Result<string, string>> GetTitleAsync(CancellationToken cancellationToken = default) =>
        DysonWindowsStaHost.InvokeAsync(() =>
            Result<string, string>.AsValue(CurrentTitle ?? BrowserControl.CoreWebView2?.DocumentTitle ?? ""));

    public Task<VoidResult<string>> NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Task.FromResult(new VoidResult<string>("url is required"));
        return WithCoreAsync(core => core.Navigate(url));
    }

    public Task<VoidResult<string>> ReloadAsync(CancellationToken cancellationToken = default) =>
        WithCoreAsync(core => core.Reload());

    /// <summary>Reload bypassing the HTTP cache (CDP <c>Page.reload</c>).</summary>
    internal async Task<VoidResult<string>> HardReloadAsync()
    {
        var result = await WithCoreAsync(async core =>
        {
            await core.CallDevToolsProtocolMethodAsync("Page.reload", """{"ignoreCache":true}""").ConfigureAwait(true);
            return Result<bool, string>.AsValue(true);
        }).ConfigureAwait(false);
        return result.IsError ? VoidResult<string>.AsError(result.Error) : VoidResult<string>.Success;
    }

    public Task<VoidResult<string>> GoBackAsync(CancellationToken cancellationToken = default) =>
        WithCoreAsync(core =>
        {
            if (core.CanGoBack)
                core.GoBack();
        });

    public Task<VoidResult<string>> GoForwardAsync(CancellationToken cancellationToken = default) =>
        WithCoreAsync(core =>
        {
            if (core.CanGoForward)
                core.GoForward();
        });

    public async Task<VoidResult<string>> ClickAsync(
        DysonBrowserClickRequest request,
        CancellationToken cancellationToken = default)
    {
        var script = DysonBrowserDomScripts.Click(request);
        if (script.IsError)
            return VoidResult<string>.AsError(script.Error);
        var result = await ExecuteJavaScriptAsync(script.Value, cancellationToken).ConfigureAwait(false);
        return DysonBrowserDomScripts.InterpretOk(result);
    }

    public async Task<VoidResult<string>> TypeAsync(
        DysonBrowserTypeRequest request,
        CancellationToken cancellationToken = default)
    {
        var script = DysonBrowserDomScripts.Type(request);
        if (script.IsError)
            return VoidResult<string>.AsError(script.Error);
        var result = await ExecuteJavaScriptAsync(script.Value, cancellationToken).ConfigureAwait(false);
        return DysonBrowserDomScripts.InterpretOk(result);
    }

    public async Task<VoidResult<string>> FillAsync(
        string selector,
        string value,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return new VoidResult<string>("selector is required");
        return await TypeAsync(new DysonBrowserTypeRequest
        {
            Selector = selector,
            Text = value ?? "",
            ClearFirst = true,
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VoidResult<string>> HoverAsync(
        string selector,
        CancellationToken cancellationToken = default)
    {
        var script = DysonBrowserDomScripts.Hover(selector);
        if (script.IsError)
            return VoidResult<string>.AsError(script.Error);
        var result = await ExecuteJavaScriptAsync(script.Value, cancellationToken).ConfigureAwait(false);
        return DysonBrowserDomScripts.InterpretOk(result);
    }

    public async Task<VoidResult<string>> PressKeyAsync(
        DysonBrowserKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        var script = DysonBrowserDomScripts.PressKey(request);
        if (script.IsError)
            return VoidResult<string>.AsError(script.Error);
        var result = await ExecuteJavaScriptAsync(script.Value, cancellationToken).ConfigureAwait(false);
        return DysonBrowserDomScripts.InterpretOk(result);
    }

    public async Task<VoidResult<string>> WaitForSelectorAsync(
        string selector,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return new VoidResult<string>("selector is required");

        var timeout = timeoutMs is > 0 ? timeoutMs.Value : DysonBrowserDefaults.DefaultTimeoutMs;
        var sel = JsonSerializer.Serialize(selector);
        var deadline = Environment.TickCount64 + timeout;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ExecuteJavaScriptAsync(DysonBrowserDomScripts.SelectorProbe(sel), cancellationToken)
                .ConfigureAwait(false);
            if (result.IsError)
                return new VoidResult<string>(result.Error);
            if (string.Equals(result.Value, "ok", StringComparison.Ordinal))
                return VoidResult<string>.Success;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return new VoidResult<string>($"Timeout waiting for selector: {selector}");
    }

    public async Task<VoidResult<string>> WaitForNavigationAsync(
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        var timeout = timeoutMs is > 0 ? timeoutMs.Value : DysonBrowserDefaults.DefaultTimeoutMs;
        TaskCompletionSource tcs;
        lock (_navGate)
        {
            _navTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs = _navTcs;
        }

        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        if (completed == tcs.Task)
        {
            await tcs.Task.ConfigureAwait(false);
            return VoidResult<string>.Success;
        }

        return new VoidResult<string>("Timeout waiting for navigation.");
    }

    public async Task<Result<string, string>> ExecuteJavaScriptAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Result<string, string>.AsError("code is required");

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Race eval against the caller's token; the STA call itself cannot be aborted.
            var evalTask = WithCoreAsync(async core =>
            {
                var r = await core.ExecuteScriptWithResultAsync(code).ConfigureAwait(true);
                if (!r.Succeeded)
                {
                    var message = r.Exception?.Message;
                    return Result<string, string>.AsError(
                        string.IsNullOrWhiteSpace(message) ? "JavaScript evaluation failed." : message);
                }

                return Result<string, string>.AsValue(DysonWebView2Interop.UnwrapScriptResult(r.ResultAsJson));
            });
            var delayTask = Task.Delay(Timeout.Infinite, cancellationToken);
            var winner = await Task.WhenAny(evalTask, delayTask).ConfigureAwait(false);
            if (winner != evalTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Result<string, string>.AsError("JavaScript evaluation timed out.");
            }

            return await evalTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError(ex.Message);
        }
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
        linked.Token.ThrowIfCancellationRequested();

        try
        {
            // CDP capture includes WebGPU/WebGL pixels (CapturePreviewAsync may not).
            var cdpTask = WithCoreAsync(async core =>
            {
                var json = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{}").ConfigureAwait(true);
                return DysonWebView2Interop.ParseScreenshotData(json);
            });
            var delayTask = Task.Delay(Timeout.Infinite, linked.Token);
            var winner = await Task.WhenAny(cdpTask, delayTask).ConfigureAwait(false);
            if (winner != cdpTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Result<byte[], string>.AsError($"Screenshot timed out after {timeout}ms.");
            }

            var shot = await cdpTask.ConfigureAwait(false);
            return shot.IsError
                ? Result<byte[], string>.AsError("TakeScreenshot not available or failed: " + shot.Error)
                : shot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<byte[], string>.AsError($"Screenshot timed out after {timeout}ms.");
        }
        catch (Exception ex)
        {
            return Result<byte[], string>.AsError("TakeScreenshot not available or failed: " + ex.Message);
        }
    }

    public Task<Result<IReadOnlyList<DysonBrowserConsoleEntry>, string>> ReadConsoleLogAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DysonBrowserConsoleEntry> list = _console.ToArray();
        return Task.FromResult(Result<IReadOnlyList<DysonBrowserConsoleEntry>, string>.AsValue(list));
    }

    public Task<Result<IReadOnlyList<DysonBrowserNetworkEntry>, string>> ReadNetworkLogAsync(
        CancellationToken cancellationToken = default)
    {
        // Main-frame navigations only (real HTTP status); no full CDP Network.* log.
        IReadOnlyList<DysonBrowserNetworkEntry> list = _network.ToArray();
        return Task.FromResult(Result<IReadOnlyList<DysonBrowserNetworkEntry>, string>.AsValue(list));
    }

    public Task<VoidResult<string>> ClearConsoleLogAsync(CancellationToken cancellationToken = default)
    {
        while (_console.TryDequeue(out _)) { }
        return Task.FromResult(VoidResult<string>.Success);
    }

    public Task<VoidResult<string>> ClearNetworkLogAsync(CancellationToken cancellationToken = default)
    {
        while (_network.TryDequeue(out _)) { }
        return Task.FromResult(VoidResult<string>.Success);
    }
}
