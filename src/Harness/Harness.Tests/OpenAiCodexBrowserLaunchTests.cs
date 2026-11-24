using System.Net;
using System.Net.Sockets;
using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Native ChatGPT Codex sign-in: Begin opens the system browser through the shared launcher (listener bound
/// first, failure reported and not swallowed), and Cancel releases port 1455 for a clean retry.
/// </summary>
public sealed class OpenAiCodexBrowserLaunchTests
{
    private static Result<TcpListener, string> BindEphemeral()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Result<TcpListener, string>.AsValue(listener);
    }

    private static OpenAiCodexAuthService NewAuth(List<OpenAiCodexCallback> made) =>
        new(
            new InMemoryOpenAiCodexCredentialStore(),
            new OpenAiCodexOAuthClient(() => new HttpClient()),
            callbackFactory: () =>
            {
                var callback = new OpenAiCodexCallback(BindEphemeral);
                made.Add(callback);
                return callback;
            });

    [Fact]
    public async Task OpenInBrowser_keeps_the_begin_on_success_and_records_the_launch_error_on_failure()
    {
        var begin = new ManagedConnectionBegin("https://auth.example/authorize?x=1", "s1");
        string? seen = null;

        var ok = await begin.OpenInBrowserAsync(url =>
        {
            seen = url;
            return Task.FromResult(VoidResult<string>.Success);
        });
        var failed = await begin.OpenInBrowserAsync(_ =>
            Task.FromResult(VoidResult<string>.AsError("Failed to open URL: no handler")));

        Assert.Equal(begin.AuthUrl, seen);
        Assert.Null(ok.BrowserLaunchError);
        Assert.Equal("Failed to open URL: no handler", failed.BrowserLaunchError);
        Assert.Equal(begin.AuthUrl, failed.AuthUrl);
        Assert.Equal(begin.State, failed.State);
    }

    [Fact]
    public async Task Cancel_releases_the_listener_port_and_a_retry_can_begin_again()
    {
        var made = new List<OpenAiCodexCallback>();
        var auth = NewAuth(made);

        var first = await auth.BeginAsync(Guid.NewGuid());
        Assert.True(first.IsSuccess);
        Assert.True(made[0].IsBound);

        await auth.CancelAsync();

        Assert.False(made[0].IsBound);
        var retry = await auth.BeginAsync(Guid.NewGuid());
        Assert.True(retry.IsSuccess);
        Assert.NotEqual(first.Value.State, retry.Value.State);
        await auth.CancelAsync();
    }

    [Fact]
    public async Task Cancel_for_a_stale_state_keeps_the_newer_login_listening()
    {
        var made = new List<OpenAiCodexCallback>();
        var auth = NewAuth(made);
        var first = await auth.BeginAsync(Guid.NewGuid());
        var second = await auth.BeginAsync(Guid.NewGuid());
        Assert.False(made[0].IsBound);
        Assert.True(made[1].IsBound);

        await auth.CancelAsync(first.Value.State);
        Assert.True(made[1].IsBound);

        await auth.CancelAsync(second.Value.State);
        Assert.False(made[1].IsBound);
    }

    [Fact]
    public async Task Provider_begin_opens_the_browser_after_the_listener_is_bound_and_reports_a_failed_launch()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _ = conn;
        var made = new List<OpenAiCodexCallback>();
        var boundWhenLaunched = false;
        var opened = new List<string>();
        var launchResult = VoidResult<string>.Success;
        var provider = new OpenAiCodexManagedInferenceProvider(
            NewAuth(made),
            new HttpClient(),
            DysonTempDb.Models(accessor),
            DysonFixedLocalSubjectContext.Instance,
            DysonTempDb.Settings(accessor),
            url =>
            {
                boundWhenLaunched = made[^1].IsBound;
                opened.Add(url);
                return Task.FromResult(launchResult);
            });
        Assert.True((await provider.ImportAsync()).IsSuccess);
        Assert.True((await provider.AcceptConsentAsync()).IsSuccess);

        var begin = await provider.BeginConnectionAsync(openBrowser: true);

        Assert.True(begin.IsSuccess);
        Assert.Equal([begin.Value.AuthUrl], opened);
        Assert.True(boundWhenLaunched);
        Assert.Null(begin.Value.BrowserLaunchError);

        launchResult = VoidResult<string>.AsError("Failed to open URL: boom");
        var failed = await provider.BeginConnectionAsync(openBrowser: true);
        Assert.True(failed.IsSuccess);
        Assert.Equal("Failed to open URL: boom", failed.Value.BrowserLaunchError);

        opened.Clear();
        var quiet = await provider.BeginConnectionAsync(openBrowser: false);
        Assert.True(quiet.IsSuccess);
        Assert.Empty(opened);
        await provider.CancelConnectionAsync();
    }
}
