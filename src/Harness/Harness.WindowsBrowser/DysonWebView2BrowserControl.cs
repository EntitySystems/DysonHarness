using System.Collections.Concurrent;
using Microsoft.Web.WebView2.Core;

namespace DysonHarness;

/// <summary>
/// Windows WebView2 implementation of <see cref="IDysonBrowserControl"/>.
/// Agent tabs use the WebView2 profile <c>agent</c>, separate from the desktop shell's <c>shell</c> profile.
/// Register as a process-wide DI singleton from Harness.UI on Windows.
/// </summary>
public sealed class DysonWebView2BrowserControl : IDysonBrowserControl
{
    private readonly ConcurrentDictionary<string, DysonWindowsBrowserWindow> _windows = new(StringComparer.Ordinal);

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
            DysonWindowsStaHost.EnsureStarted();
            var w = width is > 0 ? width.Value : 1280;
            var h = height is > 0 ? height.Value : 800;

            var window = await DysonWindowsStaHost.InvokeAsync(() =>
            {
                var win = new DysonWindowsBrowserWindow(this, url, w, h);
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
            // All agent tabs share one profile; clearing it once covers every tab. The shell profile is untouched.
            var cleared = await DysonWindowsStaHost.InvokeAsync(async () =>
            {
                var profile = tabs.Select(t => t.Profile).FirstOrDefault(p => p is not null);
                if (profile is null)
                    return false;
                await profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage)
                    .ConfigureAwait(true);
                return true;
            }).ConfigureAwait(false);
            if (!cleared)
                return Result<DysonBrowserCacheClearResult, string>.AsError("ClearBrowserCache failed: no initialized agent tab.");
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
            var reload = await tab.HardReloadAsync().ConfigureAwait(false);
            if (reload.IsError)
                firstError ??= reload.Error;
            else
                reloaded++;
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

    internal DysonWindowsBrowserWindow? TryGetWindow(string windowId) =>
        _windows.TryGetValue(windowId, out var w) ? w : null;
}
