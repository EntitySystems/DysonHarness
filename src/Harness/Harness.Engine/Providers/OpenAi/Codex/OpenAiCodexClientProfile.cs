// Derived from router-for-me/CLIProxyAPI (MIT)
//   internal/auth/codex/openai_auth.go, internal/runtime/executor/codex_executor_request.go
//   @ 6e6afab99a1939525ecb6bb1f0873c8e7a9df877. See THIRD-PARTY-NOTICES.md.

namespace DysonHarness;

/// <summary>
/// Host-level override for the native ChatGPT Codex User-Agent, bound from config section
/// <see cref="SectionName"/> (env <c>Dyson__OpenAiCodex__UserAgent</c>). Not stored per subject.
/// </summary>
public sealed class OpenAiCodexClientOptions
{
    public const string SectionName = "Dyson:OpenAiCodex";

    /// <summary>Replaces <see cref="OpenAiCodexClientProfile.DefaultUserAgent"/> when non-blank.</summary>
    public string? UserAgent { get; set; }
}

/// <summary>
/// The one place that owns ChatGPT Codex OAuth and inference identity constants.
/// Classic Codex CLI login (authorization code + PKCE). SIWC is not implemented.
/// </summary>
public static class OpenAiCodexClientProfile
{
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
    public const string TokenUrl = "https://auth.openai.com/oauth/token";

    /// <summary>Registered redirect. Host spelling is <c>localhost</c>, not <c>127.0.0.1</c>.</summary>
    public const string RedirectUri = "http://localhost:1455/auth/callback";

    public const int CallbackPort = 1455;
    public const string CallbackPath = "/auth/callback";

    public const string AuthorizeScope = "openid email profile offline_access";
    public const string RefreshScope = "openid profile email";

    /// <summary>96 random bytes, base64url without padding (128 characters).</summary>
    public const int PkceVerifierBytes = 96;

    /// <summary>OAuth state entropy (32 bytes).</summary>
    public const int StateBytes = 32;

    public const string InferenceBaseUrl = "https://chatgpt.com/backend-api/codex";
    public const string InferenceHost = "chatgpt.com";

    /// <summary>Go const <c>codexUserAgent</c>.</summary>
    public const string DefaultUserAgent =
        "codex-tui/0.154.0 (Mac OS 26.5.2; arm64) iTerm.app/3.6.11 (codex-tui; 0.154.0)";

    /// <summary>Go const <c>codexOriginator</c>. Header name is <c>Originator</c>.</summary>
    public const string Originator = "codex-tui";

    /// <summary>Header name as sent by CLIProxy (not <c>ChatGPT-Account-ID</c>).</summary>
    public const string AccountIdHeader = "Chatgpt-Account-Id";

    public const string SessionIdHeader = "Session-Id";

    /// <summary>Id-token claim object that holds the ChatGPT account.</summary>
    public const string AuthClaim = "https://api.openai.com/auth";

    /// <summary>Refresh when the access token is within this window of expiry.</summary>
    public static readonly TimeSpan RefreshLead = TimeSpan.FromMinutes(5);

    /// <summary>Bounds token and refresh HTTP calls.</summary>
    public static readonly TimeSpan OAuthHttpTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long Connect waits for the browser to hit the loopback callback.</summary>
    public static readonly TimeSpan CallbackWait = TimeSpan.FromMinutes(5);

    public static string EffectiveUserAgent(OpenAiCodexClientOptions? options) =>
        string.IsNullOrWhiteSpace(options?.UserAgent) ? DefaultUserAgent : options.UserAgent.Trim();

    /// <summary>
    /// Identity headers for chatgpt.com requests (everything except <c>Authorization</c> and
    /// <see cref="AccountIdHeader"/>, which need the stored credential).
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildHeaders(
        OpenAiCodexClientOptions? options,
        string? accountId,
        string? sessionId,
        bool stream)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = EffectiveUserAgent(options),
            ["Originator"] = Originator,
            ["Accept"] = stream ? "text/event-stream" : "application/json",
            ["Connection"] = "Keep-Alive",
        };

        if (!string.IsNullOrWhiteSpace(accountId))
            headers[AccountIdHeader] = accountId.Trim();
        if (!string.IsNullOrWhiteSpace(sessionId))
            headers[SessionIdHeader] = sessionId.Trim();

        return headers;
    }

    /// <summary>Authorize query (PKCE S256). <paramref name="state"/> and <paramref name="codeChallenge"/> are caller-supplied.</summary>
    public static string BuildAuthorizeUrl(string state, string codeChallenge)
    {
        var query = new (string Key, string Value)[]
        {
            ("response_type", "code"),
            ("client_id", ClientId),
            ("redirect_uri", RedirectUri),
            ("scope", AuthorizeScope),
            ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256"),
            ("state", state),
            ("prompt", "login"),
            ("id_token_add_organizations", "true"),
            ("codex_cli_simplified_flow", "true"),
        };

        return AuthorizeUrl + "?" + string.Join("&", query.Select(p =>
            Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    }
}
