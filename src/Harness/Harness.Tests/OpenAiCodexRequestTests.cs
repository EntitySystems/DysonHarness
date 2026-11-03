using System.Net;
using System.Text.Json.Nodes;
using DysonHarness;

namespace Harness.Tests;

public class OpenAiCodexRequestHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Non_handle_bearer_is_unchanged()
    {
        var (handler, inner, _) = Handler();
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/v1/models");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer sk-user");

        await http.SendAsync(request);

        Assert.Equal("Bearer sk-user", inner.Requests[0].Headers["Authorization"]);
        Assert.False(inner.Requests[0].Headers.ContainsKey("Chatgpt-Account-Id"));
    }

    [Fact]
    public async Task Handle_on_chatgpt_com_gets_the_token_and_codex_headers()
    {
        var (handler, inner, store) = Handler();
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCodexClientProfile.InferenceBaseUrl + "/responses");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + OpenAiCodexAuthService.HandleFor(Seed(store)));
        request.Content = new StringContent("""{"stream":true,"prompt_cache_key":"sess-9"}""", System.Text.Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = inner.Requests[0];
        Assert.Equal("Bearer access-1", sent.Headers["Authorization"]);
        Assert.Equal("acct_1", sent.Headers["Chatgpt-Account-Id"]);
        // FakeXaiHttpHandler joins multi-value headers with commas. User-Agent's wire separator is a space.
        Assert.Equal(OpenAiCodexClientProfile.DefaultUserAgent, sent.Headers["User-Agent"].Replace(',', ' '));
        Assert.Equal("codex-tui", sent.Headers["Originator"]);
        Assert.Equal("sess-9", sent.Headers["Session-Id"]);
        Assert.Equal("text/event-stream", sent.Headers["Accept"]);
        Assert.DoesNotContain("access-1", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Any_other_host_does_not_receive_the_token()
    {
        var (handler, inner, store) = Handler();
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + OpenAiCodexAuthService.HandleFor(Seed(store)));

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(inner.Requests);
        Assert.DoesNotContain("access-1", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Http_and_a_non_443_port_are_refused()
    {
        var (handler, inner, store) = Handler();
        using var http = new HttpClient(handler);
        var handle = "Bearer " + OpenAiCodexAuthService.HandleFor(Seed(store));

        using var clear = new HttpRequestMessage(HttpMethod.Get, "http://chatgpt.com/backend-api/codex/models");
        clear.Headers.TryAddWithoutValidation("Authorization", handle);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(clear)).StatusCode);

        using var port = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com:8443/backend-api/codex/models");
        port.Headers.TryAddWithoutValidation("Authorization", handle);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(port)).StatusCode);
        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task A_401_refreshes_once_and_resends()
    {
        var calls = 0;
        var (handler, inner, store) = Handler(_ =>
        {
            calls++;
            return calls == 1
                ? FakeXaiHttpHandler.Json("""{"error":"unauthorized"}""", HttpStatusCode.Unauthorized)
                : FakeXaiHttpHandler.Json("""{"ok":true}""");
        }, RefreshJson("access-2", "refresh-2"));
        var id = Seed(store);
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiCodexClientProfile.InferenceBaseUrl + "/models");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + OpenAiCodexAuthService.HandleFor(id));

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Requests.Count);
        Assert.Equal("Bearer access-2", inner.Requests[1].Headers["Authorization"]);
    }

    private static Guid Seed(InMemoryOpenAiCodexCredentialStore store)
    {
        var id = Guid.NewGuid();
        store.Put(id, new OpenAiCodexCredential
        {
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            AccountId = "acct_1",
            Expired = OpenAiCodexCredential.FormatUtc(Now.AddHours(1)),
        }.ToJson());
        return id;
    }

    private static (OpenAiCodexRequestHandler Handler, FakeXaiHttpHandler Inner, InMemoryOpenAiCodexCredentialStore Store) Handler(
        Func<CapturedRequest, HttpResponseMessage>? responder = null,
        string? refreshJson = null)
    {
        var inner = new FakeXaiHttpHandler(responder ?? (_ => FakeXaiHttpHandler.Json("""{"ok":true}""")));
        var oauthHttp = new FakeXaiHttpHandler(_ => FakeXaiHttpHandler.Json(refreshJson ?? RefreshJson("access-2", "refresh-2")));
        var store = new InMemoryOpenAiCodexCredentialStore();
        var auth = new OpenAiCodexAuthService(
            store,
            new OpenAiCodexOAuthClient(() => new HttpClient(oauthHttp, disposeHandler: false), new FixedTimeProvider(Now)),
            new FixedTimeProvider(Now));
        var handler = new OpenAiCodexRequestHandler(auth) { InnerHandler = inner };
        return (handler, inner, store);
    }

    private static string RefreshJson(string access, string refresh) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["access_token"] = access,
            ["refresh_token"] = refresh,
            ["id_token"] = "h." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                """{"email":"ada@example.com","https://api.openai.com/auth":{"chatgpt_account_id":"acct_1"}}"""))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".s",
            ["expires_in"] = 3600,
        });
}

public class OpenAiCodexRequestSanitizerTests
{
    [Fact]
    public void Drops_the_codex_reject_list_keeps_the_cache_key_and_forces_stream()
    {
        var body = new JsonObject
        {
            ["previous_response_id"] = "resp_1",
            ["prompt_cache_retention"] = "24h",
            ["safety_identifier"] = "s",
            ["stream_options"] = new JsonObject(),
            ["temperature"] = 0.2,
            ["max_output_tokens"] = 100,
            ["generate"] = true,
            ["prompt_cache_key"] = "keep-me",
            ["stream"] = false,
            ["store"] = false,
        };

        OpenAiCodexRequestSanitizer.Apply(body);

        Assert.False(body.ContainsKey("previous_response_id"));
        Assert.False(body.ContainsKey("prompt_cache_retention"));
        Assert.False(body.ContainsKey("safety_identifier"));
        Assert.False(body.ContainsKey("stream_options"));
        Assert.False(body.ContainsKey("temperature"));
        Assert.False(body.ContainsKey("max_output_tokens"));
        Assert.False(body.ContainsKey("generate"));
        Assert.Equal("keep-me", body["prompt_cache_key"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.False(body["store"]!.GetValue<bool>());
    }
}
