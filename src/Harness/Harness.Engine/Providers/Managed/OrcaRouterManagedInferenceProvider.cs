using System.Net.Http.Headers;
using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// OrcaRouter as a direct API-key managed provider (Completions, stored key + catalog browse).
/// </summary>
public sealed class OrcaRouterManagedInferenceProvider : IManagedInferenceProvider
{
    internal const string ApiBaseUrl = "https://api.orcarouter.ai/v1";

    private readonly HttpClient _http;
    private readonly IDysonModelRepository _models;

    public OrcaRouterManagedInferenceProvider(HttpClient http, IDysonModelRepository models)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(models);
        _http = http;
        _models = models;
    }

    public string ManagedSource => DysonManagedSources.OrcaRouter;
    public string DisplayName => "OrcaRouter";

    public async Task<Result<IReadOnlyList<ManagedInferenceModel>, string>> GetModelsAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return Result<IReadOnlyList<ManagedInferenceModel>, string>.AsError("API key is required.");

        var key = apiKey.Trim();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiBaseUrl + "/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = text.Length > 600 ? text[..600] + "…" : text;
                return Result<IReadOnlyList<ManagedInferenceModel>, string>.AsError(
                    $"OrcaRouter GET /models {(int)response.StatusCode}: {snippet}");
            }

            IReadOnlyList<ManagedInferenceModel> models;
            try
            {
                models = ParseModels(text);
            }
            catch (JsonException ex)
            {
                return Result<IReadOnlyList<ManagedInferenceModel>, string>.AsError(
                    $"Invalid OrcaRouter models JSON: {ex.Message}", ex);
            }

            return Result<IReadOnlyList<ManagedInferenceModel>, string>.AsValue(models);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<IReadOnlyList<ManagedInferenceModel>, string>.AsError(
                "OrcaRouter GET /models request was cancelled.");
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<ManagedInferenceModel>, string>.AsError(
                $"OrcaRouter GET /models failed: {ex.Message}", ex);
        }
    }

    public async Task<Result<Guid, string>> ImportAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return Result<Guid, string>.AsError("API key is required.");

        // syncSlugs:false so a second Import cannot wipe enabled slugs (UI also disables re-import).
        return await _models.UpsertManagedProviderAsync(
                DysonManagedSources.OrcaRouter,
                DisplayName,
                ApiBaseUrl,
                apiKey.Trim(),
                DysonOpenAiApiModes.Completions,
                slugs: [],
                shared: false,
                syncSlugs: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<VoidResult<string>> UpdateApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return VoidResult<string>.AsError("API key is required.");

        var upsert = await _models.UpsertManagedProviderAsync(
                DysonManagedSources.OrcaRouter,
                DisplayName,
                ApiBaseUrl,
                apiKey.Trim(),
                DysonOpenAiApiModes.Completions,
                slugs: [],
                shared: false,
                syncSlugs: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return upsert.IsError
            ? VoidResult<string>.AsError(upsert.Error)
            : VoidResult<string>.Success;
    }

    internal static IReadOnlyList<ManagedInferenceModel> ParseModels(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement;
        var models = new List<ManagedInferenceModel>();
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return models;
        }

        foreach (var item in data.EnumerateArray())
        {
            var mapped = MapModel(item);
            if (mapped is not null)
                models.Add(mapped);
        }

        return models;
    }

    internal static ManagedInferenceModel? MapModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return null;

        var id = idEl.GetString();
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var slug = id.Trim();
        if (IsDroppedVideoPrefix(slug) || !KeepTextOrOpenAiChat(item))
            return null;

        return new ManagedInferenceModel(slug, slug, DefaultReasoningEffort: null, EffortLevels: []);
    }

    private static bool IsDroppedVideoPrefix(string slug) =>
        slug.StartsWith("kling/", StringComparison.OrdinalIgnoreCase)
        || slug.StartsWith("byteplus/", StringComparison.OrdinalIgnoreCase);

    private static bool KeepTextOrOpenAiChat(JsonElement item)
    {
        if (item.TryGetProperty("architecture", out var architecture)
            && architecture.ValueKind == JsonValueKind.Object
            && architecture.TryGetProperty("output_modalities", out var modalities))
        {
            return modalities.ValueKind == JsonValueKind.Array
                && ArrayContainsIgnoreCase(modalities, "text");
        }

        return item.TryGetProperty("supported_endpoint_types", out var endpoints)
            && endpoints.ValueKind == JsonValueKind.Array
            && ArrayContainsIgnoreCase(endpoints, "openai");
    }

    private static bool ArrayContainsIgnoreCase(JsonElement array, string value)
    {
        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String)
                continue;

            if (string.Equals(el.GetString(), value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
