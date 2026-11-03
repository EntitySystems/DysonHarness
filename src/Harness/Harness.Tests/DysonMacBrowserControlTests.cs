using DysonHarness;

namespace Harness.Tests;

public class DysonMacBrowserControlTests
{
    [Fact]
    public async Task OpenBrowser_returns_the_native_window_id()
    {
        string? request = null;
        var control = new DysonMacBrowserControl(json =>
        {
            request = json;
            return "{\"ok\":true,\"windowId\":\"w1\",\"tabId\":\"t1\"}";
        });

        var opened = await control.OpenBrowserAsync("https://example.com", 800, 600);

        Assert.False(opened.IsError);
        Assert.Equal("w1", opened.Value.Id);
        Assert.Contains("\"op\":\"openWindow\"", request, StringComparison.Ordinal);
        Assert.Contains("https://example.com", request, StringComparison.Ordinal);
        Assert.Contains("\"width\":800", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Navigate_rejects_a_blank_url_without_calling_native()
    {
        var calls = 0;
        var tab = new DysonMacBrowserTab(_ =>
        {
            calls++;
            return "{\"ok\":true}";
        }, "w1", "t1");

        var result = await tab.NavigateAsync("  ");

        Assert.True(result.IsError);
        Assert.Equal("url is required", result.Error);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Eval_maps_the_native_timeout_error()
    {
        var tab = new DysonMacBrowserTab(
            _ => "{\"ok\":false,\"error\":\"JavaScript evaluation timed out.\"}",
            "w1",
            "t1");

        var result = await tab.ExecuteJavaScriptAsync("1+1");

        Assert.True(result.IsError);
        Assert.Equal("JavaScript evaluation timed out.", result.Error);
    }

    [Fact]
    public async Task Screenshot_decodes_png_bytes()
    {
        var png = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        var tab = new DysonMacBrowserTab(
            _ => "{\"ok\":true,\"pngBase64\":\"" + png + "\"}",
            "w1",
            "t1");

        var shot = await tab.TakeScreenshotAsync(1_000);

        Assert.False(shot.IsError);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, shot.Value);
    }

    [Fact]
    public async Task Resize_rejects_a_window_under_200()
    {
        var window = new DysonMacBrowserWindow(_ => "{\"ok\":true}", "w1");
        var result = await window.ResizeAsync(100, 800);
        Assert.True(result.IsError);
        Assert.Equal("Width and height must be at least 200.", result.Error);
    }

    [Fact]
    public void Snip_payload_keeps_url_and_percent()
    {
        var png = Convert.ToBase64String(new byte[] { 9, 8, 7 });
        DysonBrowserSnipPayload? got = null;
        var control = new DysonMacBrowserControl(_ => "{\"ok\":true}");
        control.SnipCaptured += payload => got = payload;

        control.RaiseSnip(
            "{\"imageBase64\":\"" + png + "\",\"url\":\"https://example.com/docs\",\"percentDown\":12,\"scrollY\":40}");

        Assert.NotNull(got);
        Assert.Equal(new byte[] { 9, 8, 7 }, got.ImageBytes);
        Assert.Equal("https://example.com/docs", got.Url);
        Assert.Equal(12, got.PercentDown);
        Assert.Equal(40d, got.ScrollY);
        Assert.Equal("browser-snip.jpg", got.FileName);
    }

    [Fact]
    public async Task Console_log_parses_entries()
    {
        var tab = new DysonMacBrowserTab(
            _ => "{\"ok\":true,\"console\":[{\"level\":\"error\",\"message\":\"boom\",\"source\":\"https://example.com\",\"timestamp\":\"2026-11-03T00:00:00.000Z\"}]}",
            "w1",
            "t1");

        var log = await tab.ReadConsoleLogAsync();

        Assert.False(log.IsError);
        var entry = Assert.Single(log.Value);
        Assert.Equal("error", entry.Level);
        Assert.Equal("boom", entry.Message);
        Assert.Equal("https://example.com", entry.Source);
    }
}
