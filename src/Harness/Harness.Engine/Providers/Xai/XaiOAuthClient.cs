// Derived from router-for-me/CLIProxyAPI (MIT) internal/auth/xai/xai.go @ 97f244b8ddb9cbf564b6e6faab0159102cca8617.
// See THIRD-PARTY-NOTICES.md.

using System.Text;
using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// xAI OIDC discovery, RFC 8628 device-code login and refresh. Stateless: the caller schedules polls.
/// Gets its <see cref="HttpClient"/> from <paramref name="httpFactory"/> per call (never the default client,
/// which carries the xAI handler). Error strings never include tokens.
/// </summary>
public sealed class XaiOAuthClient(
    Func<HttpClient> httpFactory,
    TimeProvider? clock = null,
    TimeSpan? minPollInterval = null)
{
    private const int MaxBodySnippet = 300;

    private readonly Func<HttpClient> _httpFactory =
        httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Floor for the poll interval and the <c>slow_down</c> step.</summary>
    public TimeSpan MinPollInterval { get; } = minPollInterval ?? XaiGrokClientProfile.DefaultPollInterval;

    /// <summary>Interval to use for a device code: server value floored at <see cref="MinPollInterval"/>.</summary>
    public TimeSpan InitialInterval(XaiDeviceCode code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var fromServer = TimeSpan.FromSeconds(Math.Max(0, code.Interval));
        return fromServer < MinPollInterval ? MinPollInterval : fromServer;
    }

    /// <summary>Interval after a <c>slow_down</c> answer.</summary>
    public TimeSpan SlowDown(TimeSpan current) => current + MinPollInterval;

    /// <summary>https and host <c>x.ai</c> or <c>*.x.ai</c> only.</summary>
    public static Result<string, string> ValidateOAuthEndpoint(string? rawUrl, string field)
    {
        var url = rawUrl?.Trim() ?? "";
        if (url.Length == 0)
            return Result<string, string>.AsError($"xai discovery {field} is empty");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return Result<string, string>.AsError($"xai discovery {field} is invalid");
        if (parsed.Scheme != Uri.UriSchemeHttps)
            return Result<string, string>.AsError($"xai discovery {field} must use https");

        var host = parsed.Host.ToLowerInvariant();
        if (host != "x.ai" && !host.EndsWith(".x.ai", StringComparison.Ordinal))
            return Result<string, string>.AsError($"xai discovery {field} host \"{host}\" is not on x.ai");

        return Result<string, string>.AsValue(url);
    }

    public async Task<Result<XaiOAuthEndpoints, string>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, XaiGrokClientProfile.DiscoveryUrl),
                "xai discovery",
                cancellationToken)
            .ConfigureAwait(false);
        if (reply.IsError)
            return Result<XaiOAuthEndpoints, string>.AsError(reply.Error);
        if (reply.Value.Status != 200)
            return Result<XaiOAuthEndpoints, string>.AsError(
                $"xai discovery failed with status {reply.Value.Status}: {Snippet(reply.Value.Body)}");

        using var doc = TryParseObject(reply.Value.Body);
        if (doc is null)
            return Result<XaiOAuthEndpoints, string>.AsError("xai discovery: unparseable response");

        var device = ValidateOAuthEndpoint(
            GetString(doc.RootElement, "device_authorization_endpoint"), "device_authorization_endpoint");
        if (device.IsError)
            return Result<XaiOAuthEndpoints, string>.AsError(device.Error);
        var token = ValidateOAuthEndpoint(GetString(doc.RootElement, "token_endpoint"), "token_endpoint");
        if (token.IsError)
            return Result<XaiOAuthEndpoints, string>.AsError(token.Error);

        return Result<XaiOAuthEndpoints, string>.AsValue(new XaiOAuthEndpoints(device.Value, token.Value));
    }

    /// <summary>Discover, then request a device code.</summary>
    public async Task<Result<XaiDeviceCode, string>> StartDeviceFlowAsync(CancellationToken cancellationToken = default)
    {
        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (discovery.IsError)
            return Result<XaiDeviceCode, string>.AsError(discovery.Error);

        return await RequestDeviceCodeAsync(
                discovery.Value.DeviceAuthorizationEndpoint,
                discovery.Value.TokenEndpoint,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<XaiDeviceCode, string>> RequestDeviceCodeAsync(
        string deviceAuthorizationEndpoint,
        string tokenEndpoint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceAuthorizationEndpoint))
            return Result<XaiDeviceCode, string>.AsError(
                "xai device code: device authorization endpoint is required");

        var reply = await SendAsync(
                () => FormRequest(
                    deviceAuthorizationEndpoint.Trim(),
                    [
                        new("client_id", XaiGrokClientProfile.ClientId),
                        new("scope", XaiGrokClientProfile.Scope),
                    ]),
                "xai device code request",
                cancellationToken)
            .ConfigureAwait(false);
        if (reply.IsError)
            return Result<XaiDeviceCode, string>.AsError(reply.Error);
        if (reply.Value.Status != 200)
            return Result<XaiDeviceCode, string>.AsError(
                $"xai device code request failed with status {reply.Value.Status}: {Snippet(reply.Value.Body)}");

        using var doc = TryParseObject(reply.Value.Body);
        if (doc is null)
            return Result<XaiDeviceCode, string>.AsError("xai device code: unparseable response");

        var root = doc.RootElement;
        var deviceCode = GetString(root, "device_code");
        var userCode = GetString(root, "user_code");
        var uri = GetString(root, "verification_uri");
        var uriComplete = GetString(root, "verification_uri_complete");
        if (string.IsNullOrWhiteSpace(deviceCode))
            return Result<XaiDeviceCode, string>.AsError("xai device code: response missing device_code");
        if (string.IsNullOrWhiteSpace(userCode))
            return Result<XaiDeviceCode, string>.AsError("xai device code: response missing user_code");
        if (string.IsNullOrWhiteSpace(uri) && string.IsNullOrWhiteSpace(uriComplete))
            return Result<XaiDeviceCode, string>.AsError("xai device code: response missing verification URI");

        return Result<XaiDeviceCode, string>.AsValue(new XaiDeviceCode(
            deviceCode!.Trim(),
            userCode!.Trim(),
            uri?.Trim(),
            uriComplete?.Trim(),
            GetInt(root, "expires_in"),
            GetInt(root, "interval"),
            tokenEndpoint?.Trim() ?? ""));
    }

    /// <summary>
    /// One device-code exchange. Pending / slow_down are values; expired, denied and other errors are
    /// error Results (the flow is over).
    /// </summary>
    public async Task<Result<XaiPollOutcome, string>> PollOnceAsync(
        XaiDeviceCode code,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (string.IsNullOrWhiteSpace(code.TokenEndpoint))
            return Result<XaiPollOutcome, string>.AsError("xai device token: token endpoint is missing");

        var reply = await SendAsync(
                () => FormRequest(
                    code.TokenEndpoint,
                    [
                        new("grant_type", XaiGrokClientProfile.DeviceCodeGrantType),
                        new("device_code", code.DeviceCode.Trim()),
                        new("client_id", XaiGrokClientProfile.ClientId),
                    ]),
                "xai device token request",
                cancellationToken)
            .ConfigureAwait(false);
        if (reply.IsError)
            return Result<XaiPollOutcome, string>.AsError(reply.Error);

        using var doc = TryParseObject(reply.Value.Body);
        if (doc is null)
            return Result<XaiPollOutcome, string>.AsError("xai device token: unparseable response");

        var error = GetString(doc.RootElement, "error");
        if (!string.IsNullOrEmpty(error))
        {
            switch (error)
            {
                case "authorization_pending":
                    return Result<XaiPollOutcome, string>.AsValue(new XaiPollOutcome(XaiPollStatus.Pending));
                case "slow_down":
                    return Result<XaiPollOutcome, string>.AsValue(new XaiPollOutcome(XaiPollStatus.SlowDown));
                case "expired_token":
                    return Result<XaiPollOutcome, string>.AsError("xai device code expired");
                case "access_denied":
                    return Result<XaiPollOutcome, string>.AsError("xai device authorization denied");
                default:
                    var desc = GetString(doc.RootElement, "error_description")?.Trim();
                    return Result<XaiPollOutcome, string>.AsError(
                        string.IsNullOrEmpty(desc)
                            ? $"xai device token error: {error}"
                            : $"xai device token error: {error}: {desc}");
            }
        }

        if (reply.Value.Status != 200)
            return Result<XaiPollOutcome, string>.AsError(
                $"xai device token request failed with status {reply.Value.Status}");

        var tokens = ParseTokenSet(doc.RootElement);
        if (tokens.IsError)
            return Result<XaiPollOutcome, string>.AsError(tokens.Error);

        return Result<XaiPollOutcome, string>.AsValue(new XaiPollOutcome(XaiPollStatus.Complete, tokens.Value));
    }

    /// <summary>Refresh an access token. Falls back to discovery when <paramref name="tokenEndpoint"/> is blank.</summary>
    public async Task<Result<XaiTokenSet, string>> RefreshAsync(
        string refreshToken,
        string? tokenEndpoint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return Result<XaiTokenSet, string>.AsError("xai token refresh: refresh token is required");

        var endpoint = tokenEndpoint?.Trim();
        if (string.IsNullOrEmpty(endpoint))
        {
            var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
            if (discovery.IsError)
                return Result<XaiTokenSet, string>.AsError(discovery.Error);
            endpoint = discovery.Value.TokenEndpoint;
        }
        else
        {
            // A stored endpoint is host-checked again so a tampered row cannot redirect the refresh token.
            var valid = ValidateOAuthEndpoint(endpoint, "token_endpoint");
            if (valid.IsError)
                return Result<XaiTokenSet, string>.AsError(valid.Error);
        }

        var rt = refreshToken.Trim();
        var reply = await SendAsync(
                () => FormRequest(
                    endpoint,
                    [
                        new("grant_type", "refresh_token"),
                        new("client_id", XaiGrokClientProfile.ClientId),
                        new("refresh_token", rt),
                    ]),
                "xai token request",
                cancellationToken)
            .ConfigureAwait(false);
        if (reply.IsError)
            return Result<XaiTokenSet, string>.AsError(reply.Error);
        if (reply.Value.Status != 200)
            return Result<XaiTokenSet, string>.AsError(
                $"xai token request failed with status {reply.Value.Status}: {Snippet(reply.Value.Body)}");

        using var doc = TryParseObject(reply.Value.Body);
        if (doc is null)
            return Result<XaiTokenSet, string>.AsError("xai token response: unparseable body");

        return ParseTokenSet(doc.RootElement);
    }

    /// <summary>Email / sub from an id_token payload. No signature verification; display only.</summary>
    public static (string Email, string Subject) ParseJwtIdentity(string? idToken)
    {
        var parts = (idToken ?? "").Split('.');
        if (parts.Length < 2)
            return ("", "");

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return ("", "");

            return (
                GetString(doc.RootElement, "email")?.Trim() ?? "",
                GetString(doc.RootElement, "sub")?.Trim() ?? "");
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return ("", "");
        }
    }

    private Result<XaiTokenSet, string> ParseTokenSet(JsonElement root)
    {
        var access = GetString(root, "access_token")?.Trim();
        if (string.IsNullOrEmpty(access))
            return Result<XaiTokenSet, string>.AsError("xai token response missing access_token");

        var idToken = GetString(root, "id_token")?.Trim() ?? "";
        var expiresIn = GetInt(root, "expires_in");
        var (email, subject) = ParseJwtIdentity(idToken);
        return Result<XaiTokenSet, string>.AsValue(new XaiTokenSet(
            access,
            GetString(root, "refresh_token")?.Trim() ?? "",
            idToken,
            GetString(root, "token_type")?.Trim() ?? "",
            expiresIn,
            expiresIn > 0 ? _clock.GetUtcNow().AddSeconds(expiresIn) : null,
            email,
            subject));
    }

    private static HttpRequestMessage FormRequest(string url, KeyValuePair<string, string>[] form) =>
        new(HttpMethod.Post, url.Trim())
        {
            Content = new FormUrlEncodedContent(form),
        };

    private readonly record struct HttpReply(int Status, string Body);

    /// <summary>Send one request with the 30 s OAuth timeout; transport failures become error Results.</summary>
    private async Task<Result<HttpReply, string>> SendAsync(
        Func<HttpRequestMessage> build,
        string what,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(XaiGrokClientProfile.OAuthHttpTimeout);
        try
        {
            using var http = _httpFactory();
            using var request = build();
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return Result<HttpReply, string>.AsValue(new HttpReply((int)response.StatusCode, body));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<HttpReply, string>.AsError($"{what} was cancelled");
        }
        catch (OperationCanceledException ex)
        {
            // Exception attached so callers can tell a transient timeout from a protocol error.
            return Result<HttpReply, string>.AsError($"{what} timed out", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return Result<HttpReply, string>.AsError($"{what} failed: {ex.Message}", ex);
        }
    }

    private static JsonDocument? TryParseObject(string body)
    {
        try
        {
            var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return doc;
            doc.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int GetInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)
            ? n
            : 0;

    /// <summary>Short, single-line body excerpt for error messages (token endpoints do not echo secrets on errors).</summary>
    internal static string Snippet(string body)
    {
        var s = body.Trim();
        return s.Length > MaxBodySnippet ? s[..MaxBodySnippet] + "…" : s;
    }
}
