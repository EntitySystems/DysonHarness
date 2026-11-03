// Derived from router-for-me/CLIProxyAPI (MIT) internal/runtime/executor/codex_executor_request.go
//   @ 6e6afab99a1939525ecb6bb1f0873c8e7a9df877. See THIRD-PARTY-NOTICES.md.
// ponytail: .NET HttpClient is not Codex and not Go uTLS. A live 403 after correct headers is a
// follow-up, not a guessed extra header. Ceiling = TLS fingerprint blocks. Upgrade = a uTLS client.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// Chained on the default <see cref="HttpClient"/>. Acts only on
/// <c>Authorization: Bearer dyson-openai-codex:&lt;guid&gt;</c>: swaps the handle for the real OAuth token,
/// stamps the Codex CLI identity headers, and retries once on a 401. Everything else passes through.
/// Token-resolution failures become synthetic HTTP responses (the one non-Result seam). Never logs tokens.
/// </summary>
public sealed class OpenAiCodexRequestHandler(OpenAiCodexAuthService auth, OpenAiCodexClientOptions? options = null)
    : DelegatingHandler
{
    private readonly OpenAiCodexAuthService _auth = auth ?? throw new ArgumentNullException(nameof(auth));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var bearer = request.Headers.Authorization;
        if (bearer is not { Scheme: "Bearer", Parameter: { } handle }
            || !OpenAiCodexAuthService.TryParseHandle(handle, out _))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var uri = request.RequestUri;
        if (!IsCodexHost(uri))
        {
            return Synthetic(request, HttpStatusCode.BadRequest,
                "ChatGPT Codex credentials are only sent to " + OpenAiCodexClientProfile.InferenceHost + " over https.");
        }

        byte[] bodyBytes = [];
        MediaTypeHeaderValue? contentType = null;
        if (request.Content is not null)
        {
            bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            contentType = request.Content.Headers.ContentType;
        }

        var (sessionId, stream) = ReadBodyHints(bodyBytes);

        var token = await _auth.GetAccessTokenAsync(handle, null, cancellationToken).ConfigureAwait(false);
        if (token.IsError)
            return Synthetic(request, HttpStatusCode.Unauthorized, token.Error);

        var access = token.Value;
        var response = await SendOnceAsync(request, bodyBytes, contentType, access, sessionId, stream, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            var refreshed = await _auth
                .GetAccessTokenAsync(handle, access.AccessToken, cancellationToken)
                .ConfigureAwait(false);
            if (refreshed.IsError)
                return Synthetic(request, HttpStatusCode.Unauthorized, refreshed.Error);

            response = await SendOnceAsync(request, bodyBytes, contentType, refreshed.Value, sessionId, stream, cancellationToken)
                .ConfigureAwait(false);
        }

        return response;
    }

    internal static bool IsCodexHost(Uri? uri) =>
        uri is not null
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, OpenAiCodexClientProfile.InferenceHost, StringComparison.OrdinalIgnoreCase)
        && (uri.IsDefaultPort || uri.Port == 443);

    private Task<HttpResponseMessage> SendOnceAsync(
        HttpRequestMessage original,
        byte[] bodyBytes,
        MediaTypeHeaderValue? contentType,
        OpenAiCodexAccess access,
        string? sessionId,
        bool stream,
        CancellationToken cancellationToken)
    {
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

        outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken);
        foreach (var (name, value) in OpenAiCodexClientProfile.BuildHeaders(options, access.AccountId, sessionId, stream))
        {
            outgoing.Headers.Remove(name);
            // TryAddWithoutValidation splits User-Agent on spaces into product tokens. The pinned
            // Codex CLI UA has spaces; the wire separator is a space, so the tokens rejoin correctly.
            outgoing.Headers.TryAddWithoutValidation(name, value);
        }

        if (original.Content is not null)
        {
            var content = new ByteArrayContent(bodyBytes);
            if (contentType is not null)
                content.Headers.ContentType = contentType;
            outgoing.Content = content;
        }

        return base.SendAsync(outgoing, cancellationToken);
    }

    /// <summary><c>prompt_cache_key</c> (copied to <c>Session-Id</c>) and <c>stream</c> from the JSON body.</summary>
    private static (string? SessionId, bool Stream) ReadBodyHints(byte[] bodyBytes)
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
            // Not JSON (a models GET has no body).
        }

        return (null, false);
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
