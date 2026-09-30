// Derived from router-for-me/CLIProxyAPI (MIT)
//   internal/auth/xai/types.go, internal/auth/xai/token.go @ 97f244b8ddb9cbf564b6e6faab0159102cca8617.
// See THIRD-PARTY-NOTICES.md.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DysonHarness;

/// <summary>Endpoints resolved from xAI OIDC discovery (already host-validated).</summary>
public sealed record XaiOAuthEndpoints(string DeviceAuthorizationEndpoint, string TokenEndpoint);

/// <summary>RFC 8628 device authorization response plus the token endpoint it must be exchanged at.</summary>
public sealed record XaiDeviceCode(
    string DeviceCode,
    string UserCode,
    string? VerificationUri,
    string? VerificationUriComplete,
    int ExpiresIn,
    int Interval,
    string TokenEndpoint)
{
    public override string ToString() => "XaiDeviceCode(redacted)";
}

/// <summary>Tokens as returned by the token endpoint (device exchange or refresh).</summary>
public sealed record XaiTokenSet(
    string AccessToken,
    string RefreshToken,
    string IdToken,
    string TokenType,
    int ExpiresIn,
    DateTimeOffset? Expire,
    string Email,
    string Subject)
{
    public override string ToString() => "XaiTokenSet(redacted)";
}

public enum XaiPollStatus
{
    Pending,
    SlowDown,
    Complete,
}

/// <summary>One device-code token exchange result. Terminal failures come back as an error Result.</summary>
public sealed record XaiPollOutcome(XaiPollStatus Status, XaiTokenSet? Tokens = null);

/// <summary>
/// Persisted xAI credential. Snake_case JSON mirrors CLIProxy's <c>xai-*.json</c> (token.go) so those
/// files import 1:1, and this same JSON is the plaintext <c>app_settings</c> row. Never log an instance.
/// </summary>
public sealed record XaiCredential
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonPropertyName("type")] public string Type { get; init; } = "xai";
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; init; } = "";
    [JsonPropertyName("id_token")] public string? IdToken { get; init; }
    [JsonPropertyName("token_type")] public string? TokenType { get; init; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; init; }
    /// <summary>Access-token expiry, RFC 3339 UTC.</summary>
    [JsonPropertyName("expired")] public string? Expired { get; init; }
    /// <summary>Last successful token acquisition, RFC 3339 UTC.</summary>
    [JsonPropertyName("last_refresh")] public string? LastRefresh { get; init; }
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("sub")] public string? Subject { get; init; }
    [JsonPropertyName("base_url")] public string? BaseUrl { get; init; }
    [JsonPropertyName("token_endpoint")] public string? TokenEndpoint { get; init; }
    [JsonPropertyName("auth_kind")] public string? AuthKind { get; init; } = "oauth";

    public override string ToString() => "XaiCredential(redacted)";

    [JsonIgnore]
    public DateTimeOffset? ExpiresAt => ParseUtc(Expired);

    /// <summary>True when the access token is missing or within <paramref name="lead"/> of expiry.</summary>
    public bool NeedsRefresh(DateTimeOffset now, TimeSpan lead) =>
        string.IsNullOrWhiteSpace(AccessToken) || ExpiresAt is not { } at || at - now <= lead;

    /// <summary>
    /// Merge refreshed/issued tokens: keeps the old refresh token when the response omits one and keeps
    /// the old identity when the new id_token carries none.
    /// </summary>
    public XaiCredential WithTokens(XaiTokenSet tokens, DateTimeOffset now, string? tokenEndpoint = null) =>
        this with
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = string.IsNullOrWhiteSpace(tokens.RefreshToken) ? RefreshToken : tokens.RefreshToken,
            IdToken = string.IsNullOrWhiteSpace(tokens.IdToken) ? IdToken : tokens.IdToken,
            TokenType = string.IsNullOrWhiteSpace(tokens.TokenType) ? TokenType : tokens.TokenType,
            ExpiresIn = tokens.ExpiresIn > 0 ? tokens.ExpiresIn : ExpiresIn,
            Expired = tokens.Expire is { } e ? FormatUtc(e) : null,
            LastRefresh = FormatUtc(now),
            Email = string.IsNullOrWhiteSpace(tokens.Email) ? Email : tokens.Email,
            Subject = string.IsNullOrWhiteSpace(tokens.Subject) ? Subject : tokens.Subject,
            TokenEndpoint = string.IsNullOrWhiteSpace(tokenEndpoint) ? TokenEndpoint : tokenEndpoint,
            Type = "xai",
            AuthKind = "oauth",
        };

    public static XaiCredential FromTokens(
        XaiTokenSet tokens,
        DateTimeOffset now,
        string tokenEndpoint,
        string baseUrl = XaiGrokClientProfile.ChatProxyBaseUrl) =>
        new XaiCredential { BaseUrl = baseUrl }.WithTokens(tokens, now, tokenEndpoint);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parse a stored row or a CLIProxy <c>xai-*.json</c> (unknown keys such as <c>headers</c> are ignored).</summary>
    public static Result<XaiCredential, string> TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result<XaiCredential, string>.AsError("xAI credential is empty.");

        try
        {
            var cred = JsonSerializer.Deserialize<XaiCredential>(json, JsonOptions);
            if (cred is null)
                return Result<XaiCredential, string>.AsError("xAI credential is not a JSON object.");
            if (string.IsNullOrWhiteSpace(cred.RefreshToken) && string.IsNullOrWhiteSpace(cred.AccessToken))
                return Result<XaiCredential, string>.AsError("xAI credential has no tokens.");

            return Result<XaiCredential, string>.AsValue(cred);
        }
        catch (JsonException)
        {
            // Deliberately no ex.Message: it can echo fragments of the (secret) payload.
            return Result<XaiCredential, string>.AsError("xAI credential JSON is corrupt.");
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
