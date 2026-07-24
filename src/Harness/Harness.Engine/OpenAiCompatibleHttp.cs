using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>Shared HTTP helpers for OpenAI-compatible Completions and Responses clients.</summary>
public static class OpenAiCompatibleHttp
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Normalize provider BaseUrl to an absolute .../v1 root (no trailing slash).</summary>
    public static string NormalizeBaseUrl(string? baseUrl)
    {
        var raw = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "https://" + raw;
        }

        raw = raw.TrimEnd('/');
        if (!raw.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            raw += "/v1";

        return raw;
    }

    public static void ApplyBearerAuth(HttpRequestMessage request, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    public static StringContent JsonContent(object body) =>
        new(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

    public static string PromptCacheKey(Guid persistenceId) =>
        persistenceId == Guid.Empty
            ? "dyson:ephemeral"
            : $"dyson:{persistenceId:N}";

    /// <summary>
    /// True when the model slug looks like a GPT-5.6+ family that may accept
    /// <c>prompt_cache_options</c> / breakpoints. Prefer omit for everyone else.
    /// </summary>
    public static bool LooksLikeGpt56OrNewer(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return false;

        var s = slug.Trim().ToLowerInvariant();
        // gpt-5.6, gpt-5.7, openai/gpt-5.6-..., etc.
        return System.Text.RegularExpressions.Regex.IsMatch(
            s,
            @"gpt-5\.(?:[6-9]|\d{2,})\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Builds OpenAI <c>tools[]</c> from the MCP catalog with required harness <c>stage</c> on every schema.
    /// Stable sort by tool name.
    /// </summary>
    public static JsonArray BuildToolsArray(DysonMcpPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        var tools = new JsonArray();
        foreach (var tool in pipeline.Tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var parameters = InjectStageIntoSchema(tool.InputSchemaJson);
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = parameters,
                },
            });
        }

        return tools;
    }

    /// <summary>Responses API tools use a flat function shape (name/description/parameters at top level).</summary>
    public static JsonArray BuildResponsesToolsArray(DysonMcpPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        var tools = new JsonArray();
        foreach (var tool in pipeline.Tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var parameters = InjectStageIntoSchema(tool.InputSchemaJson);
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = parameters,
            });
        }

        return tools;
    }

    public static JsonObject InjectStageIntoSchema(string inputSchemaJson)
    {
        JsonObject parameters;
        try
        {
            var node = JsonNode.Parse(
                string.IsNullOrWhiteSpace(inputSchemaJson) ? "{}" : inputSchemaJson);
            parameters = node as JsonObject ?? new JsonObject { ["type"] = "object" };
        }
        catch (JsonException)
        {
            parameters = new JsonObject { ["type"] = "object" };
        }

        if (parameters["type"] is null)
            parameters["type"] = "object";

        var properties = parameters["properties"] as JsonObject ?? new JsonObject();
        parameters["properties"] = properties;

        properties["stage"] = new JsonObject
        {
            ["type"] = "integer",
            ["description"] =
                "Harness execution stage (required). Same-stage calls run concurrently; " +
                "ascending stage order is a barrier between groups.",
        };

        var required = parameters["required"] as JsonArray ?? [];
        var hasStage = required.Any(n =>
            n is JsonValue v
            && v.TryGetValue<string>(out var s)
            && string.Equals(s, "stage", StringComparison.Ordinal));

        if (!hasStage)
            required.Add("stage");

        parameters["required"] = required;
        return parameters;
    }

    public static async Task<Result<JsonObject, string>> SendJsonAsync(
        HttpClient http,
        HttpMethod method,
        string url,
        string? apiKey,
        JsonObject body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            using var request = new HttpRequestMessage(method, url)
            {
                Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json"),
            };
            ApplyBearerAuth(request, apiKey);

            using var response = await http
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = text.Length > 800 ? text[..800] + "…" : text;
                return Result<JsonObject, string>.AsError(
                    $"OpenAI API {(int)response.StatusCode} {response.ReasonPhrase}: {snippet}");
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (JsonException ex)
            {
                return Result<JsonObject, string>.AsError($"Invalid JSON from OpenAI API: {ex.Message}");
            }

            if (parsed is not JsonObject obj)
                return Result<JsonObject, string>.AsError("OpenAI API returned a non-object JSON payload.");

            return Result<JsonObject, string>.AsValue(obj);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<JsonObject, string>.AsError("OpenAI API request was cancelled.");
        }
        catch (HttpRequestException ex)
        {
            return Result<JsonObject, string>.AsError($"OpenAI API HTTP error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<JsonObject, string>.AsError($"OpenAI API request failed: {ex.Message}");
        }
    }

    public static (int Stage, string ArgsWithoutStage) SplitStageFromArguments(string? argumentsJson)
    {
        var stage = 0;
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (node is not JsonObject obj)
                return (0, argumentsJson ?? "{}");

            if (obj["stage"] is JsonValue stageVal)
            {
                if (stageVal.TryGetValue<int>(out var i))
                    stage = i;
                else if (stageVal.TryGetValue<long>(out var l))
                    stage = (int)l;
                else if (int.TryParse(stageVal.ToString(), out var parsed))
                    stage = parsed;
            }

            obj.Remove("stage");
            return (stage, obj.ToJsonString(JsonOptions));
        }
        catch (JsonException)
        {
            return (0, string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        }
    }

    public static string? FormatUsageCacheHint(JsonObject response)
    {
        var usage = response["usage"] as JsonObject;
        if (usage is null)
            return null;

        var promptDetails = usage["prompt_tokens_details"] as JsonObject
            ?? usage["input_tokens_details"] as JsonObject;

        var cached = promptDetails?["cached_tokens"]?.GetValue<int?>()
            ?? usage["cached_tokens"]?.GetValue<int?>();

        var cacheWrite = promptDetails?["cache_write_tokens"]?.GetValue<int?>()
            ?? usage["cache_write_tokens"]?.GetValue<int?>();

        if (cached is null && cacheWrite is null)
            return null;

        return $"usage cache: cached_tokens={cached?.ToString() ?? "—"} cache_write_tokens={cacheWrite?.ToString() ?? "—"}";
    }

    /// <summary>
    /// POST JSON and read Server-Sent Events <c>data:</c> payloads until <c>[DONE]</c>.
    /// Yields each JSON payload string; first item may be an error Result.
    /// </summary>
    public static async IAsyncEnumerable<Result<string, string>> ReadSseJsonPayloadsAsync(
        HttpClient http,
        HttpMethod method,
        string url,
        string? apiKey,
        JsonObject body,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(body);

        string? startError = null;
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(method, url)
            {
                Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json"),
            };
            ApplyBearerAuth(request, apiKey);

            response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            startError = "OpenAI API request was cancelled.";
        }
        catch (HttpRequestException ex)
        {
            startError = $"OpenAI API HTTP error: {ex.Message}";
        }
        catch (Exception ex)
        {
            startError = $"OpenAI API request failed: {ex.Message}";
        }

        if (startError is not null || response is null)
        {
            yield return Result<string, string>.AsError(startError ?? "OpenAI API request failed.");
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var snippet = errorText.Length > 800 ? errorText[..800] + "…" : errorText;
                yield return Result<string, string>.AsError(
                    $"OpenAI API {(int)response.StatusCode} {response.ReasonPhrase}: {snippet}");
                yield break;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (true)
            {
                string? line;
                string? readError = null;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    readError = "OpenAI API stream was cancelled.";
                    line = null;
                }
                catch (IOException ex)
                {
                    readError = $"OpenAI API stream read failed: {ex.Message}";
                    line = null;
                }

                if (readError is not null)
                {
                    yield return Result<string, string>.AsError(readError);
                    yield break;
                }

                if (line is null)
                    break;

                if (line.Length == 0)
                    continue;

                // SSE comments / event names / id fields — ignore non-data lines.
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                var payload = line.Length > 5 ? line[5..].TrimStart() : "";
                if (payload.Length == 0)
                    continue;

                if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
                    break;

                yield return Result<string, string>.AsValue(payload);
            }
        }
    }
}
