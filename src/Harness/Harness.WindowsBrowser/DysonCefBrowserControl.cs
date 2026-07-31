using System.Collections.Concurrent;
using CefSharp;

namespace DysonHarness;

/// <summary>
/// Windows CefSharp implementation of <see cref="IDysonBrowserControl"/>.
/// Register as a process-wide DI singleton from Harness.UI on Windows.
/// </summary>
public sealed class DysonCefBrowserControl : IDysonBrowserControl
{
    private readonly ConcurrentDictionary<string, DysonCefBrowserWindow> _windows = new(StringComparer.Ordinal);

    public event Action<DysonBrowserSnipPayload>? SnipCaptured;

    internal void RaiseSnipCaptured(DysonBrowserSnipPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        SnipCaptured?.Invoke(payload);
    }

    public async Task<Result<IDysonBrowserWindow, string>> OpenBrowserAsync(
        string? url = null,
        int? width = null,
        int? height = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            DysonCefStaHost.EnsureStarted();
            var w = width is > 0 ? width.Value : 1280;
            var h = height is > 0 ? height.Value : 800;

            var window = await DysonCefStaHost.InvokeAsync(() =>
            {
                var win = new DysonCefBrowserWindow(this, url, w, h);
                win.Show();
                return win;
            }).ConfigureAwait(false);

            _windows[window.Id] = window;
            return Result<IDysonBrowserWindow, string>.AsValue(window);
        }
        catch (Exception ex)
        {
            return Result<IDysonBrowserWindow, string>.AsError(
                "Failed to open browser: " + ex.Message,
                exception: ex);
        }
    }

    public Task<Result<IReadOnlyList<IDysonBrowserWindow>, string>> ListWindowsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IDysonBrowserWindow> list = _windows.Values.Cast<IDysonBrowserWindow>().ToArray();
        return Task.FromResult(Result<IReadOnlyList<IDysonBrowserWindow>, string>.AsValue(list));
    }

    public Task<Result<IDysonBrowserWindow, string>> GetWindowAsync(
        string windowId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(windowId))
            return Task.FromResult(Result<IDysonBrowserWindow, string>.AsError("windowId is required"));

        if (!_windows.TryGetValue(windowId, out var window))
            return Task.FromResult(Result<IDysonBrowserWindow, string>.AsError($"Window not found: {windowId}"));

        return Task.FromResult(Result<IDysonBrowserWindow, string>.AsValue(window));
    }

    public async Task<Result<DysonBrowserCacheClearResult, string>> ClearBrowserCacheAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var windows = _windows.Values.ToArray();
        if (windows.Length == 0)
        {
            return Result<DysonBrowserCacheClearResult, string>.AsValue(new DysonBrowserCacheClearResult
            {
                Windows = 0,
                TabsReloaded = 0,
            });
        }

        var tabs = windows.SelectMany(w => w.SnapshotTabs()).ToArray();
        if (tabs.Length == 0)
        {
            return Result<DysonBrowserCacheClearResult, string>.AsValue(new DysonBrowserCacheClearResult
            {
                Windows = windows.Length,
                TabsReloaded = 0,
            });
        }

        try
        {
            await DysonCefStaHost.InvokeAsync(async () =>
            {
                using var client = tabs[0].BrowserControl.GetDevToolsClient();
                await client.Network.ClearBrowserCacheAsync().ConfigureAwait(true);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Result<DysonBrowserCacheClearResult, string>.AsError(
                "ClearBrowserCache failed: " + ex.Message,
                exception: ex);
        }

        var reloaded = 0;
        string? firstError = null;
        foreach (var tab in tabs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DysonCefStaHost.InvokeAsync(() =>
                {
                    tab.BrowserControl.Reload(ignoreCache: true);
                }).ConfigureAwait(false);
                reloaded++;
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
            }
        }

        if (reloaded == 0)
        {
            return Result<DysonBrowserCacheClearResult, string>.AsError(
                "ClearBrowserCache: all tab reloads failed"
                + (firstError is null ? "." : ": " + firstError));
        }

        return Result<DysonBrowserCacheClearResult, string>.AsValue(new DysonBrowserCacheClearResult
        {
            Windows = windows.Length,
            TabsReloaded = reloaded,
        });
    }

    internal void NotifyWindowClosed(string windowId) => _windows.TryRemove(windowId, out _);

    internal DysonCefBrowserWindow? TryGetWindow(string windowId) =>
        _windows.TryGetValue(windowId, out var w) ? w : null;
}
