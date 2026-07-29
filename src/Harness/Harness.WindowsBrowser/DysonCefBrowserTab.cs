using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows;
using CefSharp;
using CefSharp.Wpf;

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

        BrowserControl = new ChromiumWebBrowser(string.IsNullOrWhiteSpace(initialUrl) ? "about:blank" : initialUrl)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        BrowserControl.AddressChanged += (_, e) =>
        {
            CurrentAddress = e.NewValue as string ?? BrowserControl.Address;
            _window.SyncAddress(Id, CurrentAddress, CurrentTitle);
        };
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
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(request.Selector))
        {
            var sel = JsonSerializer.Serialize(request.Selector);
            var button = JsonSerializer.Serialize(request.Button ?? "left");
            var js = $$"""
                (() => {
                  const el = document.querySelector({{sel}});
                  if (!el) return 'selector not found';
                  el.dispatchEvent(new MouseEvent('click', {
                    bubbles: true, cancelable: true, view: window,
                    button: {{button}} === 'right' ? 2 : ({{button}} === 'middle' ? 1 : 0),
                    ctrlKey: {{(request.CtrlKey ? "true" : "false")}},
                    shiftKey: {{(request.ShiftKey ? "true" : "false")}},
                    altKey: {{(request.AltKey ? "true" : "false")}},
                    metaKey: {{(request.MetaKey ? "true" : "false")}}
                  }));
                  if (typeof el.click === 'function' && {{button}} === 'left') el.click();
                  return 'ok';
                })()
                """;
            var result = await ExecuteJavaScriptAsync(js, cancellationToken).ConfigureAwait(false);
            if (result.IsError)
                return new VoidResult<string>(result.Error);
            if (!string.Equals(result.Value, "ok", StringComparison.Ordinal))
                return new VoidResult<string>(result.Value);
            return VoidResult<string>.Success;
        }

        if (request.X is double x && request.Y is double y)
        {
            var js = $$"""
                (() => {
                  const el = document.elementFromPoint({{x.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, {{y.ToString(System.Globalization.CultureInfo.InvariantCulture)}});
                  if (!el) return 'no element at coordinates';
                  el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window, clientX: {{x.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, clientY: {{y.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }));
                  if (typeof el.click === 'function') el.click();
                  return 'ok';
                })()
                """;
            var result = await ExecuteJavaScriptAsync(js, cancellationToken).ConfigureAwait(false);
            if (result.IsError)
                return new VoidResult<string>(result.Error);
            if (!string.Equals(result.Value, "ok", StringComparison.Ordinal))
                return new VoidResult<string>(result.Value);
            return VoidResult<string>.Success;
        }

        return new VoidResult<string>("Click requires selector or x/y coordinates.");
    }

    public async Task<VoidResult<string>> TypeAsync(
        DysonBrowserTypeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Text))
            return new VoidResult<string>("text is required");

        var sel = request.Selector is null ? "null" : JsonSerializer.Serialize(request.Selector);
        var text = JsonSerializer.Serialize(request.Text);
        var js = $$"""
            (() => {
              const el = {{sel}} ? document.querySelector({{sel}}) : document.activeElement;
              if (!el) return 'target not found';
              el.focus();
              if ({{(request.ClearFirst ? "true" : "false")}} && 'value' in el) el.value = '';
              if ('value' in el) {
                el.value = (el.value || '') + {{text}};
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
              } else {
                el.textContent = (el.textContent || '') + {{text}};
              }
              return 'ok';
            })()
            """;
        var result = await ExecuteJavaScriptAsync(js, cancellationToken).ConfigureAwait(false);
        if (result.IsError)
            return new VoidResult<string>(result.Error);
        if (!string.Equals(result.Value, "ok", StringComparison.Ordinal))
            return new VoidResult<string>(result.Value);
        return VoidResult<string>.Success;
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
        if (string.IsNullOrWhiteSpace(selector))
            return new VoidResult<string>("selector is required");
        var sel = JsonSerializer.Serialize(selector);
        var js = $$"""
            (() => {
              const el = document.querySelector({{sel}});
              if (!el) return 'selector not found';
              el.dispatchEvent(new MouseEvent('mouseover', { bubbles: true, cancelable: true, view: window }));
              el.dispatchEvent(new MouseEvent('mouseenter', { bubbles: true, cancelable: true, view: window }));
              return 'ok';
            })()
            """;
        var result = await ExecuteJavaScriptAsync(js, cancellationToken).ConfigureAwait(false);
        if (result.IsError)
            return new VoidResult<string>(result.Error);
        if (!string.Equals(result.Value, "ok", StringComparison.Ordinal))
            return new VoidResult<string>(result.Value);
        return VoidResult<string>.Success;
    }

    public async Task<VoidResult<string>> PressKeyAsync(
        DysonBrowserKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Key))
            return new VoidResult<string>("key is required");

        var sel = request.Selector is null ? "null" : JsonSerializer.Serialize(request.Selector);
        var key = JsonSerializer.Serialize(request.Key);
        var js = $$"""
            (() => {
              const el = {{sel}} ? document.querySelector({{sel}}) : document.activeElement || document.body;
              if (!el) return 'target not found';
              el.focus();
              const opts = {
                key: {{key}}, bubbles: true, cancelable: true,
                ctrlKey: {{(request.CtrlKey ? "true" : "false")}},
                shiftKey: {{(request.ShiftKey ? "true" : "false")}},
                altKey: {{(request.AltKey ? "true" : "false")}},
                metaKey: {{(request.MetaKey ? "true" : "false")}}
              };
              el.dispatchEvent(new KeyboardEvent('keydown', opts));
              el.dispatchEvent(new KeyboardEvent('keypress', opts));
              el.dispatchEvent(new KeyboardEvent('keyup', opts));
              return 'ok';
            })()
            """;
        var result = await ExecuteJavaScriptAsync(js, cancellationToken).ConfigureAwait(false);
        if (result.IsError)
            return new VoidResult<string>(result.Error);
        if (!string.Equals(result.Value, "ok", StringComparison.Ordinal))
            return new VoidResult<string>(result.Value);
        return VoidResult<string>.Success;
    }

    public async Task<VoidResult<string>> WaitForSelectorAsync(
        string selector,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return new VoidResult<string>("selector is required");

        var timeout = timeoutMs is > 0 ? timeoutMs.Value : 15_000;
        var sel = JsonSerializer.Serialize(selector);
        var deadline = Environment.TickCount64 + timeout;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var js = $"(document.querySelector({sel}) ? 'ok' : 'missing')";
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
        var timeout = timeoutMs is > 0 ? timeoutMs.Value : 30_000;
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

        try
        {
            var response = await DysonCefStaHost.InvokeAsync(async () =>
            {
                var r = await BrowserControl.EvaluateScriptAsync(code).ConfigureAwait(true);
                return r;
            }).ConfigureAwait(false);

            if (!response.Success)
                return Result<string, string>.AsError(response.Message ?? "JavaScript evaluation failed.");

            return Result<string, string>.AsValue(response.Result?.ToString() ?? "");
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError(ex.Message);
        }
    }

    public Task<Result<string, string>> GetHtmlAsync(CancellationToken cancellationToken = default) =>
        ExecuteJavaScriptAsync("document.documentElement.outerHTML", cancellationToken);

    public async Task<Result<byte[], string>> TakeScreenshotAsync(
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        var timeout = timeoutMs is > 0 ? timeoutMs.Value : 30_000;
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
