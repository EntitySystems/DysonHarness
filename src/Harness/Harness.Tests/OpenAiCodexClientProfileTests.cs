using System.Security.Cryptography;
using System.Text;
using DysonHarness;

namespace Harness.Tests;

public class OpenAiCodexClientProfileTests
{
    [Fact]
    public void Constants_match_the_codex_cli_login()
    {
        Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", OpenAiCodexClientProfile.ClientId);
        Assert.Equal("https://auth.openai.com/oauth/authorize", OpenAiCodexClientProfile.AuthorizeUrl);
        Assert.Equal("https://auth.openai.com/oauth/token", OpenAiCodexClientProfile.TokenUrl);
        Assert.Equal("http://localhost:1455/auth/callback", OpenAiCodexClientProfile.RedirectUri);
        Assert.Equal("openid email profile offline_access", OpenAiCodexClientProfile.AuthorizeScope);
        Assert.Equal("openid profile email", OpenAiCodexClientProfile.RefreshScope);
        Assert.Equal("https://chatgpt.com/backend-api/codex", OpenAiCodexClientProfile.InferenceBaseUrl);
        Assert.Equal(96, OpenAiCodexClientProfile.PkceVerifierBytes);
        Assert.Equal(TimeSpan.FromMinutes(5), OpenAiCodexClientProfile.RefreshLead);
        Assert.Equal(TimeSpan.FromMinutes(5), OpenAiCodexClientProfile.CallbackWait);
    }

    [Fact]
    public void Authorize_url_carries_pkce_and_the_codex_flags()
    {
        var url = OpenAiCodexClientProfile.BuildAuthorizeUrl("state-1", "challenge-1");
        var query = new Uri(url).Query;

        Assert.Contains("client_id=app_EMoamEEZ73f0CkXaXp7hrann", query);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(OpenAiCodexClientProfile.RedirectUri), query);
        Assert.Contains("scope=" + Uri.EscapeDataString(OpenAiCodexClientProfile.AuthorizeScope), query);
        Assert.Contains("code_challenge=challenge-1", query);
        Assert.Contains("code_challenge_method=S256", query);
        Assert.Contains("response_type=code", query);
        Assert.Contains("state=state-1", query);
        Assert.Contains("prompt=login", query);
        Assert.Contains("id_token_add_organizations=true", query);
        Assert.Contains("codex_cli_simplified_flow=true", query);
    }

    [Fact]
    public void BuildHeaders_stamps_codex_identity_and_session()
    {
        var h = OpenAiCodexClientProfile.BuildHeaders(null, "acct-1", "sess-1", stream: true);

        Assert.Equal(OpenAiCodexClientProfile.DefaultUserAgent, h["User-Agent"]);
        Assert.Equal("codex-tui", h["Originator"]);
        Assert.Equal("acct-1", h["Chatgpt-Account-Id"]);
        Assert.Equal("sess-1", h["Session-Id"]);
        Assert.Equal("text/event-stream", h["Accept"]);
        Assert.Equal("Keep-Alive", h["Connection"]);
        Assert.False(h.ContainsKey("Authorization"));
    }

    [Fact]
    public void BuildHeaders_omits_blank_ids_and_uses_json_accept_when_not_streaming()
    {
        var h = OpenAiCodexClientProfile.BuildHeaders(null, "  ", null, stream: false);

        Assert.False(h.ContainsKey("Chatgpt-Account-Id"));
        Assert.False(h.ContainsKey("Session-Id"));
        Assert.Equal("application/json", h["Accept"]);
    }

    [Fact]
    public void UserAgent_override_replaces_only_the_user_agent()
    {
        var options = new OpenAiCodexClientOptions { UserAgent = "  custom-ua/1 " };
        var h = OpenAiCodexClientProfile.BuildHeaders(options, "a", null, stream: true);

        Assert.Equal("custom-ua/1", h["User-Agent"]);
        Assert.Equal("codex-tui", h["Originator"]);
    }

    [Fact]
    public void Default_effort_prefers_medium_then_high_then_the_middle()
    {
        Assert.Equal("medium", OpenAiCodexModelCatalog.DefaultEffort(OpenAiCodexModelCatalog.Find("gpt-5.5")!));
        Assert.Equal("high", OpenAiCodexModelCatalog.DefaultEffort(new OpenAiCodexModelInfo("x", "x", ["low", "high"])));
        Assert.Equal("low", OpenAiCodexModelCatalog.DefaultEffort(new OpenAiCodexModelInfo("x", "x", ["low"])));
        Assert.Null(OpenAiCodexModelCatalog.DefaultEffort(new OpenAiCodexModelInfo("x", "x", ["none"])));
    }

    [Fact]
    public void Pkce_verifier_is_128_chars_and_the_challenge_is_its_sha256()
    {
        var verifier = OpenAiCodexPkce.CreateVerifier();
        Assert.Equal(128, verifier.Length);
        Assert.DoesNotContain('=', verifier);
        Assert.DoesNotContain('+', verifier);
        Assert.DoesNotContain('/', verifier);

        var challenge = OpenAiCodexPkce.Challenge(verifier);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        Assert.Equal(Base64UrlNoPad(hash), challenge);
    }

    private static string Base64UrlNoPad(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public class OpenAiCodexJwtTests
{
    [Fact]
    public void Parses_account_id_and_email_from_a_synthetic_id_token()
    {
        const string payload =
            """{"email":"ada@example.com","https://api.openai.com/auth":{"chatgpt_account_id":"acct_1","chatgpt_plan_type":"plus","chatgpt_user_id":"user_1"}}""";
        var token = "hdr." + Base64Url(payload) + ".sig";

        var id = OpenAiCodexJwt.Parse(token);

        Assert.Equal("acct_1", id.AccountId);
        Assert.Equal("ada@example.com", id.Email);
        Assert.Equal("plus", id.PlanType);
        Assert.Equal("user_1", id.UserId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("a.!!!.c")]
    public void Garbage_yields_empty_not_a_throw(string? token)
    {
        var id = OpenAiCodexJwt.Parse(token);
        Assert.Equal("", id.AccountId);
        Assert.Equal("", id.Email);
    }

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
