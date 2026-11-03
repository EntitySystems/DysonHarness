// Derived from router-for-me/CLIProxyAPI (MIT)
//   internal/auth/codex/token.go, internal/auth/codex/openai_auth.go
//   @ 6e6afab99a1939525ecb6bb1f0873c8e7a9df877. See THIRD-PARTY-NOTICES.md.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DysonHarness;

/// <summary>Tokens as returned by the ChatGPT token endpoint (code exchange or refresh).</summary>
public sealed record OpenAiCodexTokenSet(
    string AccessToken,
    string RefreshToken,
    string IdToken,
    string TokenType,
    int ExpiresIn,
    DateTimeOffset? Expire,
    string AccountId,
    string Email,
    string PlanType)
{
    public override string ToString() => "OpenAiCodexTokenSet(redacted)";
}

/// <summary>
/// Persisted ChatGPT Codex credential. Snake_case JSON mirrors CLIProxy's <c>codex-*.json</c> top-level
/// fields so those files import with <see cref="TryParse"/>. This same JSON is the plaintext
/// <c>app_settings</c> row. Never log an instance.
/// <para>
/// ponytail: unknown <c>siwc</c> / <c>use_siwc</c> keys are ignored on import so a file still parses;
/// they are not refreshed and not sent. Ceiling = SIWC-only accounts fail closed (401). Upgrade = a
/// second credential partition, which this slice does not implement.
/// </para>
/// </summary>
public sealed record OpenAiCodexCredential
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonPropertyName("type")] public string Type { get; init; } = "codex";
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; init; } = "";
    [JsonPropertyName("id_token")] public string? IdToken { get; init; }
    [JsonPropertyName("account_id")] public string? AccountId { get; init; }
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("plan_type")] public string? PlanType { get; init; }
    /// <summary>Access-token expiry, RFC 3339 UTC.</summary>
    [JsonPropertyName("expired")] public string? Expired { get; init; }
    /// <summary>Last successful token acquisition, RFC 3339 UTC.</summary>
    [JsonPropertyName("last_refresh")] public string? LastRefresh { get; init; }

    public override string ToString() => "OpenAiCodexCredential(redacted)";

    [JsonIgnore]
    public DateTimeOffset? ExpiresAt => ParseUtc(Expired);

    /// <summary>True when the access token is missing or within <paramref name="lead"/> of expiry.</summary>
    public bool NeedsRefresh(DateTimeOffset now, TimeSpan lead) =>
        string.IsNullOrWhiteSpace(AccessToken) || ExpiresAt is not { } at || at - now <= lead;

    /// <summary>
    /// Merge refreshed/issued tokens: keeps the old refresh token when the response omits one and keeps
    /// the old identity when the new id token carries none.
    /// </summary>
    public OpenAiCodexCredential WithTokens(OpenAiCodexTokenSet tokens, DateTimeOffset now) =>
        this with
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = string.IsNullOrWhiteSpace(tokens.RefreshToken) ? RefreshToken : tokens.RefreshToken,
            IdToken = string.IsNullOrWhiteSpace(tokens.IdToken) ? IdToken : tokens.IdToken,
            AccountId = string.IsNullOrWhiteSpace(tokens.AccountId) ? AccountId : tokens.AccountId,
            Email = string.IsNullOrWhiteSpace(tokens.Email) ? Email : tokens.Email,
            PlanType = string.IsNullOrWhiteSpace(tokens.PlanType) ? PlanType : tokens.PlanType,
            Expired = tokens.Expire is { } e ? FormatUtc(e) : null,
            LastRefresh = FormatUtc(now),
            Type = "codex",
        };

    public static OpenAiCodexCredential FromTokens(OpenAiCodexTokenSet tokens, DateTimeOffset now) =>
        new OpenAiCodexCredential().WithTokens(tokens, now);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parse a stored row or a CLIProxy <c>codex-*.json</c> (unknown keys such as <c>siwc</c> are ignored).</summary>
    public static Result<OpenAiCodexCredential, string> TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result<OpenAiCodexCredential, string>.AsError("ChatGPT Codex credential is empty.");

        try
        {
            var cred = JsonSerializer.Deserialize<OpenAiCodexCredential>(json, JsonOptions);
            if (cred is null)
                return Result<OpenAiCodexCredential, string>.AsError("ChatGPT Codex credential is not a JSON object.");
            if (string.IsNullOrWhiteSpace(cred.RefreshToken) && string.IsNullOrWhiteSpace(cred.AccessToken))
                return Result<OpenAiCodexCredential, string>.AsError("ChatGPT Codex credential has no tokens.");

            return Result<OpenAiCodexCredential, string>.AsValue(cred);
        }
        catch (JsonException)
        {
            // Deliberately no ex.Message: it can echo fragments of the (secret) payload.
            return Result<OpenAiCodexCredential, string>.AsError("ChatGPT Codex credential JSON is corrupt.");
        }
    }

    public static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset? ParseUtc(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
}
