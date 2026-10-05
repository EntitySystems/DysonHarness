using System.Net;
using System.Text;
using DysonHarness;

namespace Harness.Tests;

public class XaiOAuthClientTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static (XaiOAuthClient Client, FakeXaiHttpHandler Handler) Create(
        Func<CapturedRequest, HttpResponseMessage> responder)
    {
        var handler = new FakeXaiHttpHandler(responder);
        return (new XaiOAuthClient(() => new HttpClient(handler, disposeHandler: false), new FixedTimeProvider(T0)), handler);
    }

    private static string Jwt(string payloadJson)
    {
        static string B64(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"none\"}")}.{B64(payloadJson)}.sig";
    }

    private const string DiscoveryJson =
        """{"device_authorization_endpoint":"https://auth.x.ai/oauth2/device","token_endpoint":"https://auth.x.ai/oauth2/token"}""";

    [Fact]
    public async Task Discover_returns_validated_endpoints()
    {
        var (client, handler) = Create(_ => FakeXaiHttpHandler.Json(DiscoveryJson));

        var result = await client.DiscoverAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("https://auth.x.ai/oauth2/device", result.Value.DeviceAuthorizationEndpoint);
        Assert.Equal("https://auth.x.ai/oauth2/token", result.Value.TokenEndpoint);
        Assert.Equal(XaiGrokClientProfile.DiscoveryUrl, handler.Requests[0].Uri.ToString());
    }

    [Theory]
    [InlineData("http://auth.x.ai/token")]
    [InlineData("https://evil.example.com/token")]
    [InlineData("https://notx.ai/token")]
    [InlineData("")]
    public void ValidateOAuthEndpoint_rejects_non_https_and_non_xai(string url)
    {
        Assert.True(XaiOAuthClient.ValidateOAuthEndpoint(url, "token_endpoint").IsError);
    }

    [Theory]
    [InlineData("https://x.ai/token")]
    [InlineData("https://auth.x.ai/token")]
    [InlineData("https://A.B.X.AI/token")]
    public void ValidateOAuthEndpoint_accepts_xai_hosts(string url)
    {
        Assert.True(XaiOAuthClient.ValidateOAuthEndpoint(url, "token_endpoint").IsSuccess);
    }

    [Fact]
    public async Task Discover_rejects_off_domain_endpoint()
    {
        var (client, _) = Create(_ => FakeXaiHttpHandler.Json(
            """{"device_authorization_endpoint":"https://evil.example/device","token_endpoint":"https://auth.x.ai/t"}"""));

        var result = await client.DiscoverAsync();

        Assert.True(result.IsError);
        Assert.Contains("not on x.ai", result.Error);
    }

    [Fact]
    public async Task Discover_non_200_is_error()
    {
        var (client, _) = Create(_ => FakeXaiHttpHandler.Json("nope", HttpStatusCode.ServiceUnavailable));

        var result = await client.DiscoverAsync();

        Assert.True(result.IsError);
        Assert.Contains("503", result.Error);
    }

    [Fact]
    public async Task RequestDeviceCode_posts_client_id_and_scope_and_maps_fields()
    {
        var (client, handler) = Create(_ => FakeXaiHttpHandler.Json(
            """{"device_code":"dc","user_code":"ABCD-1234","verification_uri":"https://x.ai/device","verification_uri_complete":"https://x.ai/device?c=ABCD-1234","expires_in":600,"interval":7}"""));

        var result = await client.RequestDeviceCodeAsync("https://auth.x.ai/oauth2/device", "https://auth.x.ai/oauth2/token");

        Assert.True(result.IsSuccess);
        Assert.Equal("dc", result.Value.DeviceCode);
        Assert.Equal("ABCD-1234", result.Value.UserCode);
        Assert.Equal("https://x.ai/device?c=ABCD-1234", result.Value.VerificationUriComplete);
        Assert.Equal(600, result.Value.ExpiresIn);
        Assert.Equal(7, result.Value.Interval);
        Assert.Equal("https://auth.x.ai/oauth2/token", result.Value.TokenEndpoint);

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("client_id=b1a00492-073a-47ea-816f-4c329264a828", req.Body);
        Assert.Contains("scope=openid+profile+email+offline_access+grok-cli%3Aaccess+api%3Aaccess", req.Body);
        Assert.Contains("application/x-www-form-urlencoded", req.Headers["Content-Type"]);
    }

    [Theory]
    [InlineData("""{"user_code":"U","verification_uri":"https://x.ai/d"}""", "device_code")]
    [InlineData("""{"device_code":"D","verification_uri":"https://x.ai/d"}""", "user_code")]
    [InlineData("""{"device_code":"D","user_code":"U"}""", "verification URI")]
    public async Task RequestDeviceCode_reports_missing_fields(string json, string missing)
    {
        var (client, _) = Create(_ => FakeXaiHttpHandler.Json(json));

        var result = await client.RequestDeviceCodeAsync("https://auth.x.ai/d", "https://auth.x.ai/t");

        Assert.True(result.IsError);
        Assert.Contains(missing, result.Error);
    }

    [Fact]
    public async Task PollOnce_walks_pending_slow_down_then_success()
    {
        var replies = new Queue<string>(
        [
            """{"error":"authorization_pending"}""",
            """{"error":"slow_down"}""",
            $$"""{"access_token":"at","refresh_token":"rt","id_token":"{{Jwt("""{"email":"me@x.ai","sub":"u1"}""")}}","token_type":"Bearer","expires_in":3600}""",
        ]);
        var (client, handler) = Create(_ =>
        {
            var next = replies.Dequeue();
            return FakeXaiHttpHandler.Json(next, next.Contains("\"error\"") ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
        });
        var code = new XaiDeviceCode("dc", "UC", "https://x.ai/d", null, 600, 5, "https://auth.x.ai/t");

        var pending = await client.PollOnceAsync(code);
        var slow = await client.PollOnceAsync(code);
        var done = await client.PollOnceAsync(code);

        Assert.Equal(XaiPollStatus.Pending, pending.Value.Status);
        Assert.Equal(XaiPollStatus.SlowDown, slow.Value.Status);
        Assert.Equal(XaiPollStatus.Complete, done.Value.Status);
        Assert.Equal("at", done.Value.Tokens!.AccessToken);
        Assert.Equal("rt", done.Value.Tokens.RefreshToken);
        Assert.Equal("me@x.ai", done.Value.Tokens.Email);
        Assert.Equal("u1", done.Value.Tokens.Subject);
        Assert.Equal(T0.AddSeconds(3600), done.Value.Tokens.Expire);

        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", handler.Requests[0].Body);
        Assert.Contains("device_code=dc", handler.Requests[0].Body);
    }

    [Theory]
    [InlineData("""{"error":"expired_token"}""", "expired")]
    [InlineData("""{"error":"access_denied"}""", "denied")]
    [InlineData("""{"error":"invalid_grant","error_description":"bad thing"}""", "invalid_grant: bad thing")]
    [InlineData("""{"error":"weird"}""", "weird")]
    public async Task PollOnce_terminal_errors_are_error_results(string json, string expectedFragment)
    {
        var (client, _) = Create(_ => FakeXaiHttpHandler.Json(json, HttpStatusCode.BadRequest));
        var code = new XaiDeviceCode("dc", "UC", "https://x.ai/d", null, 600, 5, "https://auth.x.ai/t");

        var result = await client.PollOnceAsync(code);

        Assert.True(result.IsError);
        Assert.Contains(expectedFragment, result.Error);
    }

    [Fact]
    public async Task PollOnce_200_without_access_token_is_error()
    {
        var (client, _) = Create(_ => FakeXaiHttpHandler.Json("""{"refresh_token":"rt"}"""));
        var code = new XaiDeviceCode("dc", "UC", "https://x.ai/d", null, 600, 5, "https://auth.x.ai/t");

        var result = await client.PollOnceAsync(code);

        Assert.True(result.IsError);
        Assert.Contains("missing access_token", result.Error);
    }

    [Fact]
    public void Interval_is_floored_and_slow_down_adds_the_step()
    {
        var client = new XaiOAuthClient(() => new HttpClient(), minPollInterval: TimeSpan.FromSeconds(5));
        var fast = new XaiDeviceCode("d", "u", "v", null, 600, 1, "t");
        var none = new XaiDeviceCode("d", "u", "v", null, 600, 0, "t");
        var slow = new XaiDeviceCode("d", "u", "v", null, 600, 9, "t");

        Assert.Equal(TimeSpan.FromSeconds(5), client.InitialInterval(fast));
        Assert.Equal(TimeSpan.FromSeconds(5), client.InitialInterval(none));
        Assert.Equal(TimeSpan.FromSeconds(9), client.InitialInterval(slow));
        Assert.Equal(TimeSpan.FromSeconds(14), client.SlowDown(TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void Interval_floor_can_be_injected_for_fast_tests()
    {
        var client = new XaiOAuthClient(() => new HttpClient(), minPollInterval: TimeSpan.FromMilliseconds(10));
        var code = new XaiDeviceCode("d", "u", "v", null, 600, 0, "t");

        Assert.Equal(TimeSpan.FromMilliseconds(10), client.InitialInterval(code));
    }

    [Fact]
    public async Task Refresh_posts_refresh_grant_and_returns_new_tokens()
    {
        var (client, handler) = Create(_ => FakeXaiHttpHandler.Json(
            """{"access_token":"at2","refresh_token":"rt2","token_type":"Bearer","expires_in":1800}"""));

        var result = await client.RefreshAsync("rt1", "https://auth.x.ai/oauth2/token");

        Assert.True(result.IsSuccess);
        Assert.Equal("at2", result.Value.AccessToken);
        Assert.Equal("rt2", result.Value.RefreshToken);
        Assert.Equal(T0.AddSeconds(1800), result.Value.Expire);
        var body = handler.Requests.Single().Body;
        Assert.Contains("grant_type=refresh_token", body);
        Assert.Contains("refresh_token=rt1", body);
        Assert.Contains("client_id=b1a00492-073a-47ea-816f-4c329264a828", body);
    }

    [Fact]
    public async Task Refresh_without_token_endpoint_discovers_it()
    {
        var (client, handler) = Create(req => req.Uri.AbsoluteUri.Contains("openid-configuration")
            ? FakeXaiHttpHandler.Json(DiscoveryJson)
            : FakeXaiHttpHandler.Json("""{"access_token":"at2","expires_in":60}"""));

        var result = await client.RefreshAsync("rt1", null);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://auth.x.ai/oauth2/token", handler.Requests[1].Uri.ToString());
    }

    [Fact]
    public async Task Refresh_refuses_a_tampered_off_domain_token_endpoint_without_sending_the_token()
    {
        var (client, handler) = Create(_ => FakeXaiHttpHandler.Json("{}"));

        var result = await client.RefreshAsync("rt1", "https://evil.example/token");

        Assert.True(result.IsError);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Refresh_non_200_is_error_without_leaking_the_refresh_token()
    {
        var (client, _) = Create(_ => FakeXaiHttpHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest));

        var result = await client.RefreshAsync("super-secret-rt", "https://auth.x.ai/t");

        Assert.True(result.IsError);
        Assert.Contains("400", result.Error);
        Assert.DoesNotContain("super-secret-rt", result.Error);
    }

    [Fact]
    public async Task Refresh_blank_token_is_error_and_transport_failure_is_error_result()
    {
        var (client, _) = Create(_ => throw new HttpRequestException("boom"));

        Assert.True((await client.RefreshAsync(" ", "https://auth.x.ai/t")).IsError);
        var failed = await client.RefreshAsync("rt", "https://auth.x.ai/t");
        Assert.True(failed.IsError);
        Assert.Contains("boom", failed.Error);
    }

    [Fact]
    public void ParseJwtIdentity_reads_email_and_sub_and_tolerates_garbage()
    {
        var (email, sub) = XaiOAuthClient.ParseJwtIdentity(Jwt("""{"email":" a@b.c ","sub":"s-1"}"""));
        Assert.Equal("a@b.c", email);
        Assert.Equal("s-1", sub);

        Assert.Equal(("", ""), XaiOAuthClient.ParseJwtIdentity(""));
        Assert.Equal(("", ""), XaiOAuthClient.ParseJwtIdentity("not-a-jwt"));
        Assert.Equal(("", ""), XaiOAuthClient.ParseJwtIdentity("a.!!!.c"));
    }
}

public class XaiCredentialTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Json_round_trips_and_uses_cliproxy_snake_case_keys()
    {
        var cred = new XaiCredential
        {
            AccessToken = "at",
            RefreshToken = "rt",
            Expired = "2026-09-30T13:00:00Z",
            Email = "me@x.ai",
            Subject = "u1",
            BaseUrl = XaiGrokClientProfile.ChatProxyBaseUrl,
            TokenEndpoint = "https://auth.x.ai/t",
        };

        var json = cred.ToJson();
        Assert.Contains("\"access_token\":\"at\"", json);
        Assert.Contains("\"sub\":\"u1\"", json);
        Assert.Contains("\"auth_kind\":\"oauth\"", json);
        Assert.DoesNotContain("id_token", json);

        var parsed = XaiCredential.TryParse(json);
        Assert.True(parsed.IsSuccess);
        Assert.Equal(cred, parsed.Value);
    }

    [Fact]
    public void TryParse_imports_a_cliproxy_file_and_ignores_unknown_keys()
    {
        const string file = """
            {"type":"xai","access_token":"a","refresh_token":"r","id_token":"i","token_type":"Bearer",
             "expires_in":3600,"expired":"2026-09-30T13:00:00Z","last_refresh":"2026-09-30T12:00:00Z",
             "email":"e@x.ai","sub":"s","base_url":"https://api.x.ai/v1","token_endpoint":"https://auth.x.ai/t",
             "auth_kind":"oauth","headers":{"x-grok-client-version":"1.0.13"},"disabled":false}
            """;

        var parsed = XaiCredential.TryParse(file);

        Assert.True(parsed.IsSuccess);
        var c = parsed.Value;
        Assert.Equal(("a", "r", "i", "Bearer", 3600), (c.AccessToken, c.RefreshToken, c.IdToken, c.TokenType, c.ExpiresIn));
        Assert.Equal(("e@x.ai", "s", "https://auth.x.ai/t"), (c.Email, c.Subject, c.TokenEndpoint));
        Assert.Equal(T0.AddHours(1), c.ExpiresAt);
        Assert.Equal("2026-09-30T12:00:00Z", c.LastRefresh);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void TryParse_rejects_empty_corrupt_or_tokenless_input(string json)
    {
        Assert.True(XaiCredential.TryParse(json).IsError);
    }

    [Fact]
    public void WithTokens_keeps_old_refresh_token_and_identity_when_response_omits_them()
    {
        var old = new XaiCredential { AccessToken = "a1", RefreshToken = "r1", Email = "e@x.ai", Subject = "s", IdToken = "idt" };
        var tokens = new XaiTokenSet("a2", "", "", "Bearer", 600, T0.AddSeconds(600), "", "");

        var next = old.WithTokens(tokens, T0);

        Assert.Equal("a2", next.AccessToken);
        Assert.Equal("r1", next.RefreshToken);
        Assert.Equal("e@x.ai", next.Email);
        Assert.Equal("idt", next.IdToken);
        Assert.Equal("2026-09-30T12:10:00Z", next.Expired);
        Assert.Equal("2026-09-30T12:00:00Z", next.LastRefresh);
    }

    [Fact]
    public void NeedsRefresh_honours_the_lead_window()
    {
        var cred = new XaiCredential { AccessToken = "a", RefreshToken = "r", Expired = "2026-09-30T12:10:00Z" };

        Assert.False(cred.NeedsRefresh(T0, TimeSpan.FromMinutes(5)));
        Assert.True(cred.NeedsRefresh(T0.AddMinutes(6), TimeSpan.FromMinutes(5)));
        Assert.True(new XaiCredential { AccessToken = "a", RefreshToken = "r" }.NeedsRefresh(T0, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void ToString_never_prints_secrets()
    {
        var cred = new XaiCredential { AccessToken = "SECRET-AT", RefreshToken = "SECRET-RT" };

        Assert.DoesNotContain("SECRET", cred.ToString());
    }
}
