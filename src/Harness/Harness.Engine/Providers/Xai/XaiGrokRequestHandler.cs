// Derived from router-for-me/CLIProxyAPI (MIT) internal/runtime/executor/xai_executor*.go
//   (applyXAIChatHeaders, xaiStatusErr, isXAIBadCredentialsBody) @ 97f244b8ddb9cbf564b6e6faab0159102cca8617.
// See THIRD-PARTY-NOTICES.md.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// Chained on the default <see cref="HttpClient"/>. Acts only on <c>Authorization: Bearer dyson-xai:&lt;guid&gt;</c>:
/// swaps the handle for the real OAuth token, stamps the Grok CLI identity headers, retries once on a rejected
/// token and turns xAI error bodies into readable messages. Everything else passes through untouched.
/// <para>
/// This is the one deliberate non-<see cref="Result{TValue,TError}"/> seam (the framework's
/// <see cref="DelegatingHandler.SendAsync"/> contract): token-resolution failures become synthetic HTTP
/// responses, which flow into the existing <c>FormatApiHttpError</c> path. Never logs or echoes tokens.
/// </para>
/// </summary>
public sealed class XaiGrokRequestHandler(XaiGrokAuthService auth, XaiGrokClientOptions? options = null)
    : DelegatingHandler
{
    private const int SnippetMax = 300;

    private readonly XaiGrokAuthService _auth = auth ?? throw new ArgumentNullException(nameof(auth));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var bearer = request.Headers.Authorization;
        if (bearer is not { Scheme: "Bearer", Parameter: { } handle } || !XaiGrokAuthService.TryParseHandle(handle, out _))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Never attach a token anywhere but the chat-proxy over https.
        var uri = request.RequestUri;
        if (uri is null
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, XaiGrokClientProfile.ChatProxyHost, StringComparison.OrdinalIgnoreCase))
        {
            return Synthetic(request, HttpStatusCode.BadRequest,
                "xAI credentials are only sent to " + XaiGrokClientProfile.ChatProxyHost + " over https.");
        }

        byte[] bodyBytes = [];
        MediaTypeHeaderValue? contentType = null;
        if (request.Content is not null)
        {
            bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            contentType = request.Content.Headers.ContentType;
        }

        var (conversationId, stream) = ReadBodyHints(bodyBytes);

        var token = await _auth.GetAccessTokenAsync(handle, null, cancellationToken).ConfigureAwait(false);
        if (token.IsError)
            return Synthetic(request, HttpStatusCode.Unauthorized, token.Error);

        var accessToken = token.Value;
        var response = await SendOnceAsync(request, bodyBytes, contentType, accessToken, conversationId, stream, cancellationToken)
            .ConfigureAwait(false);

        if (await IsRejectedTokenAsync(response, cancellationToken).ConfigureAwait(false))
        {
            response.Dispose();
            var refreshed = await _auth.GetAccessTokenAsync(handle, accessToken, cancellationToken).ConfigureAwait(false);
            if (refreshed.IsError)
                return Synthetic(request, HttpStatusCode.Unauthorized, refreshed.Error);

            accessToken = refreshed.Value;
            response = await SendOnceAsync(request, bodyBytes, contentType, accessToken, conversationId, stream, cancellationToken)
                .ConfigureAwait(false);

            if (await IsRejectedTokenAsync(response, cancellationToken).ConfigureAwait(false))
            {
                response.Dispose();
                return Synthetic(request, HttpStatusCode.Unauthorized,
                    "xAI rejected the sign-in even after a refresh, reconnect in Settings > Models");
            }
        }

        return await MapErrorAsync(request, response, cancellationToken).ConfigureAwait(false);
    }

    private Task<HttpResponseMessage> SendOnceAsync(
        HttpRequestMessage original,
        byte[] bodyBytes,
        MediaTypeHeaderValue? contentType,
        string accessToken,
        string? conversationId,
        bool stream,
        CancellationToken cancellationToken)
    {
        // Fresh message per attempt: a sent HttpRequestMessage cannot be reused.
        var outgoing = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
        };

        foreach (var header in original.Headers)
        {
            if (!header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        foreach (var (name, value) in XaiGrokClientProfile.BuildHeaders(options, conversationId, stream))
        {
            outgoing.Headers.Remove(name);
            outgoing.Headers.TryAddWithoutValidation(name, value);
        }

        if (original.Content is not null)
        {
            var content = new ByteArrayContent(bodyBytes);
            if (contentType is not null)
                content.Headers.ContentType = contentType;
            outgoing.Content = content;
        }

        // ResponseHeadersRead is decided by the caller's HttpClient call; we just forward the message.
        return base.SendAsync(outgoing, cancellationToken);
    }

    /// <summary>401, or 403 with xAI's bad-credentials body. Buffers small error bodies so they stay readable.</summary>
    private static async Task<bool> IsRejectedTokenAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return true;
        if (response.StatusCode != HttpStatusCode.Forbidden)
            return false;

        var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
        return IsBadCredentialsBody(body);
    }

    /// <summary>Reword 426 (client version) and free-usage-exhausted 429 into actionable errors.</summary>
    private async Task<HttpResponseMessage> MapErrorAsync(
        HttpRequestMessage request,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (status is not (426 or 429))
            return response;

        var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
        if (status == 426)
        {
            response.Dispose();
            return Synthetic(request, (HttpStatusCode)426,
                $"xAI rejected the client version (sent {XaiGrokClientProfile.EffectiveVersion(options)}). " +
                $"Raise it with the {XaiGrokClientOptions.SectionName}:ClientVersion setting " +
                $"(env Dyson__XaiGrok__ClientVersion) or update Dyson. Server said: {Snippet(body)}");
        }

        if (body.Contains("free-usage-exhausted", StringComparison.OrdinalIgnoreCase))
        {
            response.Dispose();
            return Synthetic(request, HttpStatusCode.TooManyRequests,
                $"xAI free usage is exhausted (free-usage-exhausted); retrying will not help until the quota resets " +
                $"or the plan is upgraded. Server said: {Snippet(body)}");
        }

        return response;
    }

    internal static bool IsBadCredentialsBody(string body) =>
        body.Contains("bad-credentials", StringComparison.OrdinalIgnoreCase)
        || body.Contains("access token could not be validated", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content is null)
            return "";

        // Buffered, so the same content can still be returned to the caller unchanged.
        await response.Content.LoadIntoBufferAsync(1_000_000, ct).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary><c>prompt_cache_key</c> (for <c>x-grok-conv-id</c>) and <c>stream</c> from the JSON body.</summary>
    private static (string? ConversationId, bool Stream) ReadBodyHints(byte[] bodyBytes)
    {
        if (bodyBytes.Length == 0)
            return (null, false);

        try
        {
            if (JsonNode.Parse(bodyBytes) is JsonObject obj)
            {
                var key = obj["prompt_cache_key"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                var stream = obj["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var b) && b;
                return (key, stream);
            }
        }
        catch (JsonException)
        {
            // Not JSON (e.g. a models GET has no body); headers just omit the conversation id.
        }

        return (null, false);
    }

    private static string Snippet(string body)
    {
        var s = body.Trim();
        return s.Length > SnippetMax ? s[..SnippetMax] + "…" : s;
    }

    private static HttpResponseMessage Synthetic(HttpRequestMessage request, HttpStatusCode status, string message) =>
        new(status)
        {
            RequestMessage = request,
            Content = new StringContent(
                new JsonObject { ["error"] = new JsonObject { ["message"] = message } }.ToJsonString(),
                Encoding.UTF8,
                "application/json"),
        };
}
