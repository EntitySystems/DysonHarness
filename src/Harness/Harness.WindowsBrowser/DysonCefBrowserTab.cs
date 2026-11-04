using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using CefSharp;
using CefSharp.Wpf.HwndHost;

namespace DysonHarness;

internal sealed class DysonCefBrowserTab : IDysonBrowserTab
{
    private readonly DysonCefBrowserWindow _window;
    private readonly ConcurrentQueue<DysonBrowserConsoleEntry> _console = new();
    private readonly ConcurrentQueue<DysonBrowserNetworkEntry> _network = new();
    private readonly object _navGate = new();
    private TaskCompletionSource? _navTcs;

    public DysonCefBrowserTab(DysonCefBrowserWindow window, string? initialUrl)
    {
        _window = window;
        Id = Guid.NewGuid().ToString("N");
        WindowId = window.Id;

        // Windowed HwndHost so WebGPU/WebGL present into a real HWND (OSR bitmap path stays black).
        // Snip uses a CDP screenshot overlay because HWND airspace blocks live WPF rubber-bands.
        BrowserControl = new ChromiumWebBrowser(string.IsNullOrWhiteSpace(initialUrl) ? "about:blank" : initialUrl)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ActivateBrowserOnCreation = true,
            BrowserSettings = new BrowserSettings
            {
                WebGl = CefState.Enabled,
            },
        };
        // Agent windows only. The shell OSR view does not set PermissionHandler.
        BrowserControl.PermissionHandler = new DysonAgentBrowserPermissionHandler();

        BrowserControl.IsBrowserInitializedChanged += (_, _) => NudgeHwndLayoutIfNeeded();
        BrowserControl.Loaded += (_, _) => NudgeHwndLayoutIfNeeded();

        // HwndHost exposes Address as a DP but no AddressChanged event (unlike OSR Wpf).
        DependencyPropertyDescriptor
            .FromProperty(ChromiumWebBrowser.AddressProperty, typeof(ChromiumWebBrowser))
            ?.AddValueChanged(BrowserControl, (_, _) =>
            {
                CurrentAddress = BrowserControl.Address;
                _window.SyncAddress(Id, CurrentAddress, CurrentTitle);
            });
        BrowserControl.TitleChanged += (_, e) =>
        {
            CurrentTitle = e.NewValue as string ?? BrowserControl.Title;
            _window.SyncAddress(Id, CurrentAddress, CurrentTitle);
        };
        BrowserControl.LoadingStateChanged += (_, e) =>
        {
            if (!e.IsLoading)
            {
                lock (_navGate)
                {
                    _navTcs?.TrySetResult();
                    _navTcs = null;
                }
            }
        };
        BrowserControl.ConsoleMessage += (_, e) =>
        {
            _console.Enqueue(new DysonBrowserConsoleEntry
            {
                Level = e.Level.ToString(),
                Message = e.Message ?? "",
                Source = e.Source,
                Line = e.Line,
                Timestamp = DateTimeOffset.UtcNow,
            });
        };
        BrowserControl.FrameLoadEnd += (_, e) =>
        {
            if (e.Frame.IsMain)
            {
                _network.Enqueue(new DysonBrowserNetworkEntry
                {
                    Url = e.Url ?? CurrentAddress ?? "",
                    Method = "GET",
                    Status = 200,
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }
        };
    }

    public string Id { get; }
    public string WindowId { get; }
    public ChromiumWebBrowser BrowserControl { get; }
    public string? CurrentAddress { get; private set; }
    public string? CurrentTitle { get; private set; }

    /// <summary>
    /// Force a layout pass when the HwndHost is still ~0×0 so WebGPU swapchains are not created against an empty HWND.
    /// </summary>
    private void NudgeHwndLayoutIfNeeded()
    {
        if (BrowserControl.ActualWidth >= 1 && BrowserControl.ActualHeight >= 1)
            return;

        BrowserControl.InvalidateMeasure();
        BrowserControl.InvalidateArrange();
        BrowserControl.UpdateLayout();
        _window.InvalidateMeasure();
        _window.UpdateLayout();
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
        DysonCefStaHost.InvokeAsync(() =>
            Result<string, string>.AsValue(CurrentAddress ?? BrowserControl.Address ?? ""));

    public Task<Result<string, string>> GetTitleAsync(CancellationToken cancellationToken = default) =>
        DysonCefStaHost.InvokeAsync(() =>
            Result<string, string>.AsValue(CurrentTitle ?? BrowserControl.Title ?? ""));

    public Task<VoidResult<string>> NavigateAsync(string url, CancellationToken cancellationToken = default) =>
        DysonCefStaHost.InvokeAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(url))
                return new VoidResult<string>("url is required");
            BrowserControl.Load(url);
            return VoidResult<string>.Success;
        });

    public Task<VoidResult<string>> ReloadAsync(CancellationToken cancellationToken = default) =>
        DysonCefStaHost.InvokeAsync(() =>
        {
            BrowserControl.Reload();
            return VoidResult<string>.Success;
        });

    public Task<VoidResult<string>> GoBackAsync(CancellationToken cancellationToken = default) =>
        DysonCefStaHost.InvokeAsync(() =>
        {
            if (BrowserControl.CanGoBack)
                BrowserControl.Back();
            return VoidResult<string>.Success;
        });

    public Task<VoidResult<string>> GoForwardAsync(CancellationToken cancellationToken = default) =>
        DysonCefStaHost.InvokeAsync(() =>
        {
            if (BrowserControl.CanGoForward)
                BrowserControl.Forward();
            return VoidResult<string>.Success;
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
            var js = DysonBrowserDomScripts.SelectorProbe(sel);
            var result = await ExecuteJavaScriptAsync(js, cancellationToken).ConfigureAwait(false);
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

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.Token.ThrowIfCancellationRequested();

        try
        {
            // Race eval against linked CT — do not plumb CT into STA host (this change).
            var evalTask = DysonCefStaHost.InvokeAsync(async () =>
            {
                var r = await BrowserControl.EvaluateScriptAsync(code).ConfigureAwait(true);
                return r;
            });
            var delayTask = Task.Delay(Timeout.Infinite, linked.Token);
            var winner = await Task.WhenAny(evalTask, delayTask).ConfigureAwait(false);
            if (winner != evalTask)
            {
                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();
                return Result<string, string>.AsError("JavaScript evaluation timed out.");
            }

            var response = await evalTask.ConfigureAwait(false);
            if (!response.Success)
                return Result<string, string>.AsError(response.Message ?? "JavaScript evaluation failed.");

            return Result<string, string>.AsValue(response.Result?.ToString() ?? "");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<string, string>.AsError("JavaScript evaluation timed out.");
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
            // Race CDP against linked CT — do not plumb CT into STA host (this change).
            var cdpTask = DysonCefStaHost.InvokeAsync(async () =>
            {
                using var client = BrowserControl.GetDevToolsClient();
                var shot = await client.Page.CaptureScreenshotAsync().ConfigureAwait(true);
                return shot.Data;
            });
            var delayTask = Task.Delay(Timeout.Infinite, linked.Token);
            var winner = await Task.WhenAny(cdpTask, delayTask).ConfigureAwait(false);
            if (winner != cdpTask)
            {
                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();
                return Result<byte[], string>.AsError($"Screenshot timed out after {timeout}ms.");
            }

            var bytes = await cdpTask.ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
                return Result<byte[], string>.AsError("Screenshot capture returned empty.");
            return Result<byte[], string>.AsValue(bytes);
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
            return Result<byte[], string>.AsError(
                "TakeScreenshot not available or failed: " + ex.Message);
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
        // Thin stub: only main-frame load ends until CDP request logging is wired.
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
