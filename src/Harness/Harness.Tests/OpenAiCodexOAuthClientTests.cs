using System.Net;
using System.Text.Json;
using DysonHarness;

namespace Harness.Tests;

public class OpenAiCodexOAuthClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Exchange_posts_the_authorization_code_form_without_a_scope()
    {
        var http = new FakeXaiHttpHandler(_ => FakeXaiHttpHandler.Json(TokenJson()));
        var client = new OpenAiCodexOAuthClient(() => new HttpClient(http, disposeHandler: false), new FixedTimeProvider(Now));

        var result = await client.ExchangeCodeAsync("the-code", "the-verifier");

        Assert.True(result.IsSuccess);
        Assert.Equal("access-1", result.Value.AccessToken);
        Assert.Equal("refresh-1", result.Value.RefreshToken);
        Assert.Equal("acct_1", result.Value.AccountId);
        Assert.Equal("ada@example.com", result.Value.Email);
        var body = http.Requests[0].Body;
        Assert.Contains("grant_type=authorization_code", body);
        Assert.Contains("client_id=" + Uri.EscapeDataString(OpenAiCodexClientProfile.ClientId), body);
        Assert.Contains("code=the-code", body);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(OpenAiCodexClientProfile.RedirectUri), body);
        Assert.Contains("code_verifier=the-verifier", body);
        Assert.DoesNotContain("scope=", body);
        Assert.Equal(OpenAiCodexClientProfile.TokenUrl, http.Requests[0].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Non_200_exchange_is_an_error()
    {
        var http = new FakeXaiHttpHandler(_ => FakeXaiHttpHandler.Json("""{"error":"invalid_request"}""", HttpStatusCode.BadRequest));
        var client = new OpenAiCodexOAuthClient(() => new HttpClient(http, disposeHandler: false));

        var result = await client.ExchangeCodeAsync("code", "verifier");

        Assert.True(result.IsError);
        Assert.Contains("400", result.Error);
    }

    [Fact]
    public async Task Refresh_uses_the_narrower_scope_and_does_not_retry_invalid_grant()
    {
        var calls = 0;
        var http = new FakeXaiHttpHandler(_ =>
        {
            calls++;
            return FakeXaiHttpHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        });
        var client = new OpenAiCodexOAuthClient(() => new HttpClient(http, disposeHandler: false));

        var result = await client.RefreshAsync("old-refresh");

        Assert.True(result.IsError);
        Assert.Equal(1, calls);
        Assert.True(OpenAiCodexOAuthClient.IsNonRetryableRefreshError(result.Error));
        var body = http.Requests[0].Body;
        Assert.Contains("grant_type=refresh_token", body);
        Assert.Contains("scope=openid+profile+email", body);
        Assert.Contains("refresh_token=old-refresh", body);
        Assert.DoesNotContain(result.Error, "old-refresh");
    }

    [Fact]
    public void WithTokens_keeps_the_previous_refresh_token_when_the_response_omits_one()
    {
        var existing = OpenAiCodexCredential.FromTokens(Token("access-old", "refresh-old"), Now);
        var next = existing.WithTokens(Token("access-new", ""), Now.AddMinutes(1));

        Assert.Equal("access-new", next.AccessToken);
        Assert.Equal("refresh-old", next.RefreshToken);
        Assert.Equal("acct_1", next.AccountId);
    }

    private static OpenAiCodexTokenSet Token(string access, string refresh) =>
        new(access, refresh, "", "bearer", 3600, Now.AddHours(1), "acct_1", "ada@example.com", "plus");

    private static string TokenJson()
    {
        var id = "h." + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(
                """{"email":"ada@example.com","https://api.openai.com/auth":{"chatgpt_account_id":"acct_1","chatgpt_plan_type":"plus"}}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".s";
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["access_token"] = "access-1",
            ["refresh_token"] = "refresh-1",
            ["id_token"] = id,
            ["token_type"] = "bearer",
            ["expires_in"] = 3600,
        });
    }
}

public class OpenAiCodexCredentialTests
{
    [Fact]
    public void TryParse_reads_a_cliproxy_codex_file_and_ignores_siwc()
    {
        const string json = """
            {
              "type": "codex",
              "access_token": "access-1",
              "refresh_token": "refresh-1",
              "id_token": "id-1",
              "account_id": "acct_9",
              "email": "ada@example.com",
              "plan_type": "plus",
              "expired": "2026-08-14T13:00:00Z",
              "last_refresh": "2026-08-14T12:00:00Z",
              "siwc": { "access_token": "siwc-secret" },
              "use_siwc": true
            }
            """;

        var parsed = OpenAiCodexCredential.TryParse(json);

        Assert.True(parsed.IsSuccess);
        Assert.Equal("codex", parsed.Value.Type);
        Assert.Equal("access-1", parsed.Value.AccessToken);
        Assert.Equal("refresh-1", parsed.Value.RefreshToken);
        Assert.Equal("acct_9", parsed.Value.AccountId);
        Assert.Equal("ada@example.com", parsed.Value.Email);
        Assert.Equal("plus", parsed.Value.PlanType);
        Assert.DoesNotContain("siwc-secret", parsed.Value.ToJson());
        Assert.Equal("OpenAiCodexCredential(redacted)", parsed.Value.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void TryParse_rejects_bad_input_without_echoing_it(string? json)
    {
        var parsed = OpenAiCodexCredential.TryParse(json);
        Assert.True(parsed.IsError);
        if (!string.IsNullOrEmpty(json))
            Assert.DoesNotContain(json, parsed.Error);
    }
}
