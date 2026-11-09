#if WINDOWS
using DysonHarness;
using Microsoft.Web.WebView2.Core;

namespace Harness.Tests;

/// <summary>Pure WebView2 helpers (no runtime needed): script unwrap, CDP parsing, permissions, external links.</summary>
public class DysonWebView2InteropTests
{
    [Theory]
    [InlineData("\"ok\"", "ok")]
    [InlineData("\"a \\\"q\\\" b\"", "a \"q\" b")]
    [InlineData("null", "")]
    [InlineData("", "")]
    [InlineData("42", "42")]
    [InlineData("true", "true")]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    public void UnwrapScriptResult_matches_the_old_raw_value(string json, string expected) =>
        Assert.Equal(expected, DysonWebView2Interop.UnwrapScriptResult(json));

    [Fact]
    public void ParseScreenshotData_decodes_base64_and_rejects_empty()
    {
        var ok = DysonWebView2Interop.ParseScreenshotData("{\"data\":\"AQID\"}");
        Assert.False(ok.IsError);
        Assert.Equal(new byte[] { 1, 2, 3 }, ok.Value);
        Assert.True(DysonWebView2Interop.ParseScreenshotData("{}").IsError);
        Assert.True(DysonWebView2Interop.ParseScreenshotData("{\"data\":\"%%\"}").IsError);
    }

    [Fact]
    public void ParseConsoleEvent_reads_console_calls_and_exceptions()
    {
        var at = DateTimeOffset.UnixEpoch;
        var log = DysonWebView2Interop.ParseConsoleEvent(
            "Runtime.consoleAPICalled",
            """{"type":"warning","args":[{"type":"string","value":"hi"},{"type":"number","value":2}],"stackTrace":{"callFrames":[{"url":"http://x/a.js","lineNumber":4}]}}""",
            at);
        Assert.NotNull(log);
        Assert.Equal("Warning", log.Level);
        Assert.Equal("hi 2", log.Message);
        Assert.Equal("http://x/a.js", log.Source);
        Assert.Equal(5, log.Line);

        var thrown = DysonWebView2Interop.ParseConsoleEvent(
            "Runtime.exceptionThrown",
            """{"exceptionDetails":{"text":"Uncaught","lineNumber":0,"url":"http://x/b.js","exception":{"description":"Error: boom"}}}""",
            at);
        Assert.NotNull(thrown);
        Assert.Equal("Error", thrown.Level);
        Assert.Equal("Error: boom", thrown.Message);
        Assert.Equal(1, thrown.Line);

        Assert.Null(DysonWebView2Interop.ParseConsoleEvent("Runtime.consoleAPICalled", "not json", at));
    }

    [Fact]
    public void DecidePermission_agent_allows_all_shell_mic_only()
    {
        foreach (var kind in Enum.GetValues<CoreWebView2PermissionKind>())
            Assert.Equal(CoreWebView2PermissionState.Allow, DysonWebView2Interop.DecidePermission(kind, isShell: false));

        Assert.Equal(CoreWebView2PermissionState.Allow,
            DysonWebView2Interop.DecidePermission(CoreWebView2PermissionKind.Microphone, isShell: true));
        Assert.Equal(CoreWebView2PermissionState.Deny,
            DysonWebView2Interop.DecidePermission(CoreWebView2PermissionKind.Geolocation, isShell: true));
        Assert.Equal(CoreWebView2PermissionState.Deny,
            DysonWebView2Interop.DecidePermission(CoreWebView2PermissionKind.ClipboardRead, isShell: true));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000/sessions", false)]
    [InlineData("http://127.0.0.1:5001/", true)]
    [InlineData("https://github.com/x", true)]
    [InlineData("about:blank", false)]
    [InlineData("data:text/html,hi", false)]
    [InlineData("edge://gpu", false)]
    public void ShouldOpenExternally_keeps_app_origin_and_internal_schemes(string url, bool external) =>
        Assert.Equal(external, DysonWebView2Interop.ShouldOpenExternally(new Uri(url), new Uri("http://127.0.0.1:5000/")));

    [Theory]
    [InlineData("{\"type\":\"theme\",\"theme\":\"light\"}", "light")]
    [InlineData("{\"type\":\"other\",\"theme\":\"light\"}", null)]
    [InlineData("\"light\"", null)]
    [InlineData("nope", null)]
    public void ParseThemeMessage_reads_the_shim_payload(string json, string? expected) =>
        Assert.Equal(expected, DysonWebView2Interop.ParseThemeMessage(json));

    [Fact]
    public async Task SingleInstance_second_claim_signals_the_first()
    {
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var name = "DysonHarness-test-" + Guid.NewGuid().ToString("N");
        var first = DysonSingleInstance.Claim(name, () => activated.TrySetResult());
        Assert.False(first.IsError);
        using var owner = first.Value;
        Assert.True(owner.IsFirst);

        var second = DysonSingleInstance.Claim(name, () => { });
        Assert.False(second.IsError);
        using var peer = second.Value;
        Assert.False(peer.IsFirst);

        await activated.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
#endif
