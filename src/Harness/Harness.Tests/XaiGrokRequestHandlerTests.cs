using System.Net;
using System.Text;
using DysonHarness;

namespace Harness.Tests;

public class XaiGrokRequestHandlerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private const string ChatUrl = "https://cli-chat-proxy.grok.com/v1/responses";
    private const string Body = """{"model":"grok-4.7","stream":true,"prompt_cache_key":"dyson:abc:sp0","input":[]}""";

    private sealed class Rig
    {
        public required HttpClient Client { get; init; }
        public required FakeXaiHttpHandler Inner { get; init; }
        public required FakeXaiHttpHandler OAuth { get; init; }
        public required InMemoryXaiCredentialStore Store { get; init; }
        public required Guid Id { get; init; }
        public string Handle => XaiGrokAuthService.HandleFor(Id);
    }

    private static Rig CreateRig(
        Func<CapturedRequest, HttpResponseMessage> inner,
        Func<CapturedRequest, HttpResponseMessage>? oauth = null,
        XaiGrokClientOptions? options = null,
        bool seed = true)
    {
        var clock = new FixedTimeProvider(T0);
        var oauthHttp = new FakeXaiHttpHandler(oauth ?? (_ => FakeXaiHttpHandler.Json(
            """{"access_token":"at2","refresh_token":"rt2","expires_in":3600}""")));
        var store = new InMemoryXaiCredentialStore();
        var id = Guid.NewGuid();
        if (seed)
        {
            store.Put(id, new XaiCredential
            {
                AccessToken = "at1",
                RefreshToken = "rt1",
                Expired = XaiCredential.FormatUtc(T0.AddHours(1)),
                TokenEndpoint = "https://auth.x.ai/oauth2/token",
            }.ToJson());
        }

        var auth = new XaiGrokAuthService(
            store,
            new XaiOAuthClient(() => new HttpClient(oauthHttp, disposeHandler: false), clock),
            clock);
        var innerHttp = new FakeXaiHttpHandler(inner);
        var handler = new XaiGrokRequestHandler(auth, options) { InnerHandler = innerHttp };
        return new Rig
        {
            Client = new HttpClient(handler),
            Inner = innerHttp,
            OAuth = oauthHttp,
            Store = store,
            Id = id,
        };
    }

    private static HttpRequestMessage Post(string url, string bearer, string body = Body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        return request;
    }

    private static HttpResponseMessage Ok() => FakeXaiHttpHandler.Json("""{"ok":true}""");

    [Fact]
    public async Task Non_sentinel_bearer_passes_through_untouched()
    {
        var rig = CreateRig(_ => Ok());

        using var response = await rig.Client.SendAsync(
            Post("https://api.openai.com/v1/responses", "sk-plain"));

        var seen = Assert.Single(rig.Inner.Requests);
        Assert.Equal("Bearer sk-plain", seen.Headers["Authorization"]);
        Assert.False(seen.Headers.ContainsKey("X-XAI-Token-Auth"));
        Assert.False(seen.Headers.ContainsKey("x-grok-client-version"));
        Assert.Equal(Body, seen.Body);
        Assert.Empty(rig.OAuth.Requests);
    }

    [Fact]
    public async Task Sentinel_is_replaced_by_real_token_and_full_header_set()
    {
        var rig = CreateRig(_ => Ok());

        using var response = await rig.Client.SendAsync(Post(ChatUrl, rig.Handle));

        Assert.True(response.IsSuccessStatusCode);
        var seen = Assert.Single(rig.Inner.Requests);
        Assert.Equal("Bearer at1", seen.Headers["Authorization"]);
        Assert.DoesNotContain("dyson-xai", seen.Headers["Authorization"]);
        Assert.Equal("xai-grok-cli", seen.Headers["X-XAI-Token-Auth"]);
        Assert.Equal("1.0.13", seen.Headers["x-grok-client-version"]);
        Assert.Equal("xai-grok-workspace/1.0.13", seen.Headers["User-Agent"]);
        Assert.Equal("grok-shell", seen.Headers["x-grok-client-identifier"]);
        Assert.Equal("authenticate-response", seen.Headers["x-authenticateresponse"]);
        Assert.Equal("dyson:abc:sp0", seen.Headers["x-grok-conv-id"]);
        Assert.Equal("text/event-stream", seen.Headers["Accept"]);
        Assert.Equal(Body, seen.Body);
        Assert.Contains("application/json", seen.Headers["Content-Type"]);
    }

    [Fact]
    public async Task Version_override_and_extra_headers_apply()
    {
        var options = new XaiGrokClientOptions { ClientVersion = "2.0.1" };
        options.ExtraHeaders["X-Extra"] = "yes";
        var rig = CreateRig(_ => Ok(), options: options);

        using var response = await rig.Client.SendAsync(Post(ChatUrl, rig.Handle));

        var seen = Assert.Single(rig.Inner.Requests);
        Assert.Equal("2.0.1", seen.Headers["x-grok-client-version"]);
        Assert.Equal("xai-grok-workspace/2.0.1", seen.Headers["User-Agent"]);
        Assert.Equal("yes", seen.Headers["X-Extra"]);
    }

    [Theory]
    [InlineData("https://evil.example.com/v1/responses")]
    [InlineData("http://cli-chat-proxy.grok.com/v1/responses")]
    [InlineData("https://api.x.ai/v1/responses")]
    public async Task Wrong_host_or_http_is_refused_and_leaks_no_token(string url)
    {
        var rig = CreateRig(_ => Ok());

        using var response = await rig.Client.SendAsync(Post(url, rig.Handle));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(rig.Inner.Requests);
        Assert.Empty(rig.OAuth.Requests);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("at1", text);
    }

    [Fact]
    public async Task Unauthorized_triggers_exactly_one_refresh_and_one_resend()
    {
        var rig = CreateRig(req => req.Headers["Authorization"] == "Bearer at1"
            ? FakeXaiHttpHandler.Json("""{"error":"expired"}""", HttpStatusCode.Unauthorized)
            : Ok());

        using var response = await rig.Client.SendAsync(Post(ChatUrl, rig.Handle));

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(2, rig.Inner.Requests.Count);
        Assert.Equal("Bearer at2", rig.Inner.Requests[1].Headers["Authorization"]);
        Assert.Equal(Body, rig.Inner.Requests[1].Body);
        Assert.Single(rig.OAuth.Requests);
    }

    [Fact]
    public async Task Forbidden_bad_credentials_is_treated_like_401()
    {
        var rig = CreateRig(req => req.Headers["Authorization"] == "Bearer at1"
            ? FakeXaiHttpHandler.Json(
                """{"error":{"code":"bad-credentials","message":"The access token could not be validated"}}""",
                HttpStatusCode.Forbidden)
            : Ok());

        using var response = await rig.Client.SendAsync(Post(ChatUrl, rig.Handle));

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(2, rig.Inner.Requests.Count);
        Assert.Single(rig.OAuth.Requests);
    }

    [Fact]
    public async Task Other_403_is_returned_as_is_without_refresh()
    {
        var rig = CreateRig(_ => FakeXaiHttpHandler.Json("""{"error":"forbidden region"}""", HttpStatusCode.Forbidden));

        using var response = await rig.Client.SendAsync(Post(ChatUrl, rig.Handle));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("forbidden region", await response.Content.ReadAsStringAsync());
        Assert.Single(rig.Inner.Requests);
        Assert.Empty(rig.OAuth.Requests);
    }

    [Fact]
    public async Task Persistent_401_stops_after_one_resend_with_a_reconnect_message()
    {
        var rig = CreateRig(_ => FakeXaiHttpHandler.Json("{}", HttpStatusCode.Unauthorized));

        using var response = await rig.Client.SendAsync(Post(ChatUrl, rig.Handle));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(2, rig.Inner.Requests.Count);
        Assert.Contains("reconnect", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Refresh_failure_becomes_a_synthetic_401()
    {
        var rig = CreateRig(
            _ => FakeXaiHttpHandler.Json("{}", HttpStatusCode.Unauthorized),
            _ => FakeXaiHttpHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest));

        using var response = await rig.Client.SendAsync(Post(ChatUrl, rig.Handle));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("reconnect", text);
        Assert.DoesNotContain("rt1", text);
        Assert.Single(rig.Inner.Requests);
    }

    [Fact]
    public async Task Missing_credential_becomes_a_synthetic_401_without_calling_xai()
    {
        var rig = CreateRig(_ => Ok(), seed: false);

        using var response = await rig.Client.SendAsync(Post(ChatUrl, rig.Handle));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(rig.Inner.Requests);
    }

    [Fact]
    public async Task Status_426_names_the_sent_version_and_the_override_key()
    {
        var options = new XaiGrokClientOptions { ClientVersion = "0.9.0" };
        var rig = CreateRig(
            _ => FakeXaiHttpHandler.Json("""{"error":"Your Grok CLI version is outdated"}""", (HttpStatusCode)426),
            options: options);

        using var response = await rig.Client.SendAsync(Post(ChatUrl, rig.Handle));

        Assert.Equal(426, (int)response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("0.9.0", text);
        Assert.Contains("Dyson:XaiGrok:ClientVersion", text);
        Assert.Contains("Dyson__XaiGrok__ClientVersion", text);
        Assert.Contains("outdated", text);
        Assert.Single(rig.Inner.Requests);
    }

    [Fact]
    public async Task Free_usage_exhausted_429_is_not_transient_but_plain_429_is()
    {
        var exhausted = CreateRig(_ => FakeXaiHttpHandler.Json(
            """{"error":{"code":"free-usage-exhausted"}}""", HttpStatusCode.TooManyRequests));
        using var r1 = await exhausted.Client.SendAsync(Post(ChatUrl, exhausted.Handle));
        var text1 = await r1.Content.ReadAsStringAsync();
        var error1 = OpenAiCompatibleHttp.FormatApiHttpError((int)r1.StatusCode, r1.ReasonPhrase, text1);

        Assert.Equal(HttpStatusCode.TooManyRequests, r1.StatusCode);
        Assert.Contains("free usage", text1);
        Assert.False(OpenAiCompatibleHttp.IsTransientServerError(error1));

        var plain = CreateRig(_ => FakeXaiHttpHandler.Json("""{"error":"slow down"}""", HttpStatusCode.TooManyRequests));
        using var r2 = await plain.Client.SendAsync(Post(ChatUrl, plain.Handle));
        var error2 = OpenAiCompatibleHttp.FormatApiHttpError(
            (int)r2.StatusCode, r2.ReasonPhrase, await r2.Content.ReadAsStringAsync());
        Assert.True(OpenAiCompatibleHttp.IsTransientServerError(error2));
    }

    [Fact]
    public async Task Get_without_body_uses_json_accept_and_no_conv_id()
    {
        var rig = CreateRig(_ => Ok());
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://cli-chat-proxy.grok.com/v1/models");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + rig.Handle);

        using var response = await rig.Client.SendAsync(request);

        var seen = Assert.Single(rig.Inner.Requests);
        Assert.Equal("application/json", seen.Headers["Accept"]);
        Assert.False(seen.Headers.ContainsKey("x-grok-conv-id"));
        Assert.Equal("Bearer at1", seen.Headers["Authorization"]);
    }
}
