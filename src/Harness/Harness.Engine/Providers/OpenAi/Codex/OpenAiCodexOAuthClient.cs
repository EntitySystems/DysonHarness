// Derived from router-for-me/CLIProxyAPI (MIT) internal/auth/codex/openai_auth.go
//   @ 6e6afab99a1939525ecb6bb1f0873c8e7a9df877. See THIRD-PARTY-NOTICES.md.

using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// ChatGPT authorization-code + PKCE token exchange and refresh. Stateless. Gets its
/// <see cref="HttpClient"/> from <paramref name="httpFactory"/> per call (never the default client,
/// which carries the Codex handler). Error strings never include tokens.
/// </summary>
public sealed class OpenAiCodexOAuthClient(Func<HttpClient> httpFactory, TimeProvider? clock = null)
{
    private const int MaxBodySnippet = 300;

    private static readonly string[] NonRetryable =
    [
        "refresh_token_reused",
        "refresh_token_expired",
        "refresh_token_invalidated",
        "token_revoked",
        "invalid_grant",
        "invalid_client",
        "401",
    ];

    private readonly Func<HttpClient> _httpFactory =
        httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>True when a refresh error means "sign in again", not "try once more".</summary>
    public static bool IsNonRetryableRefreshError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return false;

        var text = error.ToLowerInvariant();
        return NonRetryable.Any(text.Contains);
    }

    /// <summary>Exchange an authorization code. Form fields: grant, client id, code, redirect, verifier. No scope.</summary>
    public Task<Result<OpenAiCodexTokenSet, string>> ExchangeCodeAsync(
        string code,
        string codeVerifier,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Task.FromResult(Result<OpenAiCodexTokenSet, string>.AsError("ChatGPT code exchange: code is required"));
        if (string.IsNullOrWhiteSpace(codeVerifier))
            return Task.FromResult(Result<OpenAiCodexTokenSet, string>.AsError("ChatGPT code exchange: verifier is required"));

        return PostTokenAsync(
            [
                new("grant_type", "authorization_code"),
                new("client_id", OpenAiCodexClientProfile.ClientId),
                new("code", code.Trim()),
                new("redirect_uri", OpenAiCodexClientProfile.RedirectUri),
                new("code_verifier", codeVerifier),
            ],
            "ChatGPT code exchange",
            cancellationToken);
    }

    /// <summary>
    /// Refresh an access token. The form uses the narrower refresh scope. A non-200 (including
    /// <c>invalid_grant</c>) is an error Result — this method does not retry.
    /// </summary>
    public Task<Result<OpenAiCodexTokenSet, string>> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return Task.FromResult(Result<OpenAiCodexTokenSet, string>.AsError("ChatGPT token refresh: refresh token is required"));

        return PostTokenAsync(
            [
                new("client_id", OpenAiCodexClientProfile.ClientId),
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken.Trim()),
                new("scope", OpenAiCodexClientProfile.RefreshScope),
            ],
            "ChatGPT token refresh",
            cancellationToken);
    }

    private async Task<Result<OpenAiCodexTokenSet, string>> PostTokenAsync(
        KeyValuePair<string, string>[] form,
        string what,
        CancellationToken cancellationToken)
    {
        var reply = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Post, OpenAiCodexClientProfile.TokenUrl)
                {
                    Content = new FormUrlEncodedContent(form),
                },
                what,
                cancellationToken)
            .ConfigureAwait(false);
        if (reply.IsError)
            return Result<OpenAiCodexTokenSet, string>.AsError(reply.Error);
        if (reply.Value.Status != 200)
        {
            return Result<OpenAiCodexTokenSet, string>.AsError(
                $"{what} failed with status {reply.Value.Status}: {Snippet(reply.Value.Body)}");
        }

        using var doc = TryParseObject(reply.Value.Body);
        if (doc is null)
            return Result<OpenAiCodexTokenSet, string>.AsError($"{what}: unparseable response");

        return ParseTokenSet(doc.RootElement);
    }

    private Result<OpenAiCodexTokenSet, string> ParseTokenSet(JsonElement root)
    {
        var access = GetString(root, "access_token")?.Trim();
        if (string.IsNullOrEmpty(access))
            return Result<OpenAiCodexTokenSet, string>.AsError("ChatGPT token response missing access_token");

        var idToken = GetString(root, "id_token")?.Trim() ?? "";
        var identity = OpenAiCodexJwt.Parse(idToken);
        var expiresIn = GetInt(root, "expires_in");
        return Result<OpenAiCodexTokenSet, string>.AsValue(new OpenAiCodexTokenSet(
            access,
            GetString(root, "refresh_token")?.Trim() ?? "",
            idToken,
            GetString(root, "token_type")?.Trim() ?? "",
            expiresIn,
            expiresIn > 0 ? _clock.GetUtcNow().AddSeconds(expiresIn) : null,
            identity.AccountId,
            identity.Email,
            identity.PlanType));
    }

    private readonly record struct HttpReply(int Status, string Body);

    private async Task<Result<HttpReply, string>> SendAsync(
        Func<HttpRequestMessage> build,
        string what,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OpenAiCodexClientProfile.OAuthHttpTimeout);
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

    internal static string Snippet(string body)
    {
        var s = body.Trim();
        return s.Length > MaxBodySnippet ? s[..MaxBodySnippet] + "…" : s;
    }
}
