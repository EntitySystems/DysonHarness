using System.Net;
using DysonHarness;

namespace Harness.Tests;

public class XaiGrokAuthServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private const string TokenEndpoint = "https://auth.x.ai/oauth2/token";

    private sealed record Rig(
        XaiGrokAuthService Auth,
        InMemoryXaiCredentialStore Store,
        FakeXaiHttpHandler Http,
        FixedTimeProvider Clock,
        Guid Id)
    {
        public string Handle => XaiGrokAuthService.HandleFor(Id);
    }

    private static Rig CreateRig(
        Func<CapturedRequest, HttpResponseMessage>? responder = null,
        XaiCredential? seed = null)
    {
        var clock = new FixedTimeProvider(T0);
        var http = new FakeXaiHttpHandler(responder ?? (_ => FakeXaiHttpHandler.Json(
            """{"access_token":"at-new","refresh_token":"rt-new","expires_in":3600}""")));
        var oauth = new XaiOAuthClient(() => new HttpClient(http, disposeHandler: false), clock);
        var store = new InMemoryXaiCredentialStore();
        var id = Guid.NewGuid();
        if (seed is not null)
            store.Put(id, seed.ToJson());

        return new Rig(new XaiGrokAuthService(store, oauth, clock), store, http, clock, id);
    }

    private static XaiCredential Cred(string access = "at-old", string refresh = "rt-old", int expiresInMinutes = 60) =>
        new()
        {
            AccessToken = access,
            RefreshToken = refresh,
            Expired = XaiCredential.FormatUtc(T0.AddMinutes(expiresInMinutes)),
            TokenEndpoint = TokenEndpoint,
            Email = "me@x.ai",
        };

    [Fact]
    public void Handle_round_trips_and_rejects_foreign_values()
    {
        var id = Guid.NewGuid();
        Assert.True(XaiGrokAuthService.TryParseHandle(XaiGrokAuthService.HandleFor(id), out var parsed));
        Assert.Equal(id, parsed);
        Assert.False(XaiGrokAuthService.TryParseHandle("sk-abc", out _));
        Assert.False(XaiGrokAuthService.TryParseHandle("dyson-xai:not-a-guid", out _));
        Assert.False(XaiGrokAuthService.TryParseHandle(null, out _));
    }

    [Fact]
    public async Task Fresh_token_is_returned_without_refresh()
    {
        var rig = CreateRig(seed: Cred());

        var token = await rig.Auth.GetAccessTokenAsync(rig.Handle);

        Assert.Equal("at-old", token.Value);
        Assert.Empty(rig.Http.Requests);
    }

    [Fact]
    public async Task Token_within_five_minutes_of_expiry_refreshes_and_persists_before_returning()
    {
        var rig = CreateRig(seed: Cred(expiresInMinutes: 4));
        string? rowSeenBySave = null;
        rig.Store.OnSave = (_, json) => rowSeenBySave = json;

        var token = await rig.Auth.GetAccessTokenAsync(rig.Handle);

        Assert.True(token.IsSuccess);
        Assert.Equal("at-new", token.Value);
        Assert.Single(rig.Http.Requests);
        Assert.Contains("refresh_token=rt-old", rig.Http.Requests[0].Body);
        Assert.Equal(["get", "save"], rig.Store.Calls);
        // The store already held the rotated tokens when the call returned.
        var stored = XaiCredential.TryParse(rig.Store.Peek(rig.Id)).Value;
        Assert.Equal("at-new", stored.AccessToken);
        Assert.Equal("rt-new", stored.RefreshToken);
        Assert.Equal(rowSeenBySave, rig.Store.Peek(rig.Id));
        Assert.Equal("me@x.ai", stored.Email);
    }

    [Fact]
    public async Task Concurrent_callers_cause_exactly_one_refresh()
    {
        var rig = CreateRig(seed: Cred(expiresInMinutes: 1));

        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => rig.Auth.GetAccessTokenAsync(rig.Handle)));

        Assert.All(results, r => Assert.Equal("at-new", r.Value));
        Assert.Single(rig.Http.Requests);
    }

    [Fact]
    public async Task Rejected_token_forces_one_refresh_even_when_not_near_expiry()
    {
        var rig = CreateRig(seed: Cred());

        var first = await rig.Auth.GetAccessTokenAsync(rig.Handle, rejectedAccessToken: "at-old");
        // A second caller that also got a 401 for the same old token must reuse the rotated one.
        var second = await rig.Auth.GetAccessTokenAsync(rig.Handle, rejectedAccessToken: "at-old");

        Assert.Equal("at-new", first.Value);
        Assert.Equal("at-new", second.Value);
        Assert.Single(rig.Http.Requests);
    }

    [Fact]
    public async Task Failed_save_returns_error_and_no_token()
    {
        var rig = CreateRig(seed: Cred(expiresInMinutes: 1));
        rig.Store.FailSaves = true;

        var token = await rig.Auth.GetAccessTokenAsync(rig.Handle);

        Assert.True(token.IsError);
        Assert.DoesNotContain("at-new", token.Error);
        Assert.Contains("Could not save", token.Error);
    }

    [Fact]
    public async Task Refresh_failure_is_error_without_leaking_tokens()
    {
        var rig = CreateRig(
            _ => FakeXaiHttpHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest),
            Cred(expiresInMinutes: 1));

        var token = await rig.Auth.GetAccessTokenAsync(rig.Handle);

        Assert.True(token.IsError);
        Assert.Contains("reconnect", token.Error);
        Assert.DoesNotContain("rt-old", token.Error);
    }

    [Fact]
    public async Task Missing_corrupt_and_non_handle_inputs_are_error_results()
    {
        var rig = CreateRig();
        Assert.True((await rig.Auth.GetAccessTokenAsync(rig.Handle)).IsError);

        rig.Store.Put(rig.Id, "{ this is not json SECRET-VALUE");
        var corrupt = await rig.Auth.GetAccessTokenAsync(rig.Handle);
        Assert.True(corrupt.IsError);
        Assert.Contains("Sign in again", corrupt.Error);
        Assert.DoesNotContain("SECRET-VALUE", corrupt.Error);

        Assert.True((await rig.Auth.GetAccessTokenAsync("sk-plain")).IsError);
    }

    [Fact]
    public async Task Disconnect_deletes_the_row()
    {
        var rig = CreateRig(seed: Cred());

        Assert.True((await rig.Auth.IsConnectedAsync(rig.Handle)).Value);
        Assert.Equal("me@x.ai", (await rig.Auth.GetEmailAsync(rig.Handle)).Value);
        Assert.True((await rig.Auth.DisconnectAsync(rig.Handle)).IsSuccess);

        Assert.Null(rig.Store.Peek(rig.Id));
        Assert.False((await rig.Auth.IsConnectedAsync(rig.Handle)).Value);
    }

    private static string DeviceReply => """
        {"device_code":"dc","user_code":"ABCD","verification_uri":"https://x.ai/d","verification_uri_complete":"https://x.ai/d?c=ABCD","expires_in":600,"interval":5}
        """;

    [Fact]
    public async Task Device_flow_respects_interval_slow_down_and_saves_credential_on_complete()
    {
        var polls = new Queue<string>(
        [
            """{"error":"authorization_pending"}""",
            """{"error":"slow_down"}""",
            """{"access_token":"at-1","refresh_token":"rt-1","expires_in":3600}""",
        ]);
        var rig = CreateRig(req =>
        {
            if (req.Uri.AbsoluteUri.Contains("openid-configuration"))
                return FakeXaiHttpHandler.Json(
                    """{"device_authorization_endpoint":"https://auth.x.ai/oauth2/device","token_endpoint":"https://auth.x.ai/oauth2/token"}""");
            if (req.Uri.AbsoluteUri.EndsWith("/device", StringComparison.Ordinal))
                return FakeXaiHttpHandler.Json(DeviceReply);
            var next = polls.Dequeue();
            return FakeXaiHttpHandler.Json(next, next.Contains("error") ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
        });

        var begin = await rig.Auth.BeginAsync(rig.Id);
        Assert.True(begin.IsSuccess);
        Assert.Equal("ABCD", begin.Value.UserCode);
        Assert.Equal(5, begin.Value.IntervalSeconds);
        var state = begin.Value.State;
        var networkBefore = rig.Http.Requests.Count;

        // Too early: no network, still pending.
        var early = await rig.Auth.PollAsync(state, DysonSubjects.Local);
        Assert.Equal(XaiPollStatus.Pending, early.Value.Status);
        Assert.InRange(early.Value.RetryAfterSeconds, 1, 5);
        Assert.Equal(networkBefore, rig.Http.Requests.Count);

        rig.Clock.Now = T0.AddSeconds(6);
        var pending = await rig.Auth.PollAsync(state, DysonSubjects.Local);
        Assert.Equal(XaiPollStatus.Pending, pending.Value.Status);

        rig.Clock.Now = T0.AddSeconds(12);
        var slow = await rig.Auth.PollAsync(state, DysonSubjects.Local);
        Assert.Equal(XaiPollStatus.SlowDown, slow.Value.Status);
        Assert.Equal(10, slow.Value.RetryAfterSeconds); // 5 s + 5 s step

        // slow_down pushed the next allowed poll out to +10 s.
        rig.Clock.Now = T0.AddSeconds(17);
        var stillWaiting = await rig.Auth.PollAsync(state, DysonSubjects.Local);
        Assert.Equal(XaiPollStatus.Pending, stillWaiting.Value.Status);

        rig.Clock.Now = T0.AddSeconds(23);
        var done = await rig.Auth.PollAsync(state, DysonSubjects.Local);
        Assert.Equal(XaiPollStatus.Complete, done.Value.Status);

        var stored = XaiCredential.TryParse(rig.Store.Peek(rig.Id)).Value;
        Assert.Equal("at-1", stored.AccessToken);
        Assert.Equal("rt-1", stored.RefreshToken);
        Assert.Equal(TokenEndpoint, stored.TokenEndpoint);
        Assert.Equal(XaiGrokClientProfile.ChatProxyBaseUrl, stored.BaseUrl);

        // Flow is consumed.
        Assert.True((await rig.Auth.PollAsync(state, DysonSubjects.Local)).IsError);
    }

    [Fact]
    public async Task Device_flow_denied_or_expired_ends_the_flow_with_an_error()
    {
        var rig = CreateRig(req =>
        {
            if (req.Uri.AbsoluteUri.Contains("openid-configuration"))
                return FakeXaiHttpHandler.Json(
                    """{"device_authorization_endpoint":"https://auth.x.ai/d","token_endpoint":"https://auth.x.ai/t"}""");
            if (req.Uri.AbsoluteUri.EndsWith("/d", StringComparison.Ordinal))
                return FakeXaiHttpHandler.Json(DeviceReply);
            return FakeXaiHttpHandler.Json("""{"error":"access_denied"}""", HttpStatusCode.BadRequest);
        });
        var begin = await rig.Auth.BeginAsync(rig.Id);
        rig.Clock.Now = T0.AddSeconds(6);

        var denied = await rig.Auth.PollAsync(begin.Value.State, DysonSubjects.Local);

        Assert.True(denied.IsError);
        Assert.Contains("denied", denied.Error);
        Assert.Null(rig.Store.Peek(rig.Id));
        Assert.True((await rig.Auth.PollAsync(begin.Value.State, DysonSubjects.Local)).IsError);
    }

    [Fact]
    public async Task Device_flow_expires_after_expires_in_and_cancel_forgets_it()
    {
        var rig = CreateRig(req =>
        {
            if (req.Uri.AbsoluteUri.Contains("openid-configuration"))
                return FakeXaiHttpHandler.Json(
                    """{"device_authorization_endpoint":"https://auth.x.ai/d","token_endpoint":"https://auth.x.ai/t"}""");
            return FakeXaiHttpHandler.Json(DeviceReply);
        });
        var first = await rig.Auth.BeginAsync(rig.Id);
        var second = await rig.Auth.BeginAsync(rig.Id);

        rig.Auth.CancelFlow(second.Value.State);
        Assert.True((await rig.Auth.PollAsync(second.Value.State, DysonSubjects.Local)).IsError);

        rig.Clock.Now = T0.AddSeconds(601);
        var expired = await rig.Auth.PollAsync(first.Value.State, DysonSubjects.Local);
        Assert.True(expired.IsError);
        Assert.Contains("expired", expired.Error);
    }
}
