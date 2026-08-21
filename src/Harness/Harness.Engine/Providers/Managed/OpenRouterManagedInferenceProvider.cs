using System.Net.Http.Headers;
using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// OpenRouter as a direct API-key managed provider (Completions, stored key + catalog browse).
/// </summary>
public sealed class OpenRouterManagedInferenceProvider : IManagedInferenceProvider
{
    internal const string ApiBaseUrl = "https://openrouter.ai/api/v1";

    // ponytail: 10 pages / 5000 models catalog ceiling; raise if OpenRouter pagination grows past this.
    internal const int MaxCatalogPages = 10;
    internal const int MaxCatalogModels = 5000;

    internal static readonly string[] GatewayEffortLevels =
        ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    private readonly HttpClient _http;
    private readonly IDysonModelRepository _models;

    public OpenRouterManagedInferenceProvider(HttpClient http, IDysonModelRepository models)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(models);
        _http = http;
        _models = models;
    }

    public string ManagedSource => DysonManagedSources.OpenRouter;
    public string DisplayName => "OpenRouter";

    public async Task<Result<IReadOnlyList<ManagedInferenceModel>, string>> GetModelsAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return Result<IReadOnlyList<ManagedInferenceModel>, string>.AsError("API key is required.");

        var key = apiKey.Trim();
        var collected = new List<ManagedInferenceModel>();
        var url = ApiBaseUrl + "/models?output_modalities=text";

        try
        {
            for (var page = 0; page < MaxCatalogPages && collected.Count < MaxCatalogModels; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var snippet = text.Length > 600 ? text[..600] + "…" : text;
                    return Result<IReadOnlyList<ManagedInferenceModel>, string>.AsError(
                        $"OpenRouter GET /models {(int)response.StatusCode}: {snippet}");
                }

                OpenRouterModelsPage parsed;
                try
                {
                    parsed = ParseModelsPage(text);
                }
                catch (JsonException ex)
                {
                    return Result<IReadOnlyList<ManagedInferenceModel>, string>.AsError(
                        $"Invalid OpenRouter models JSON: {ex.Message}", ex);
                }

                foreach (var model in parsed.Models)
                {
                    if (collected.Count >= MaxCatalogModels)
                        break;
                    collected.Add(model);
                }

                if (string.IsNullOrWhiteSpace(parsed.NextLink))
                    break;

                url = ResolveNextUrl(url, parsed.NextLink);
            }

            return Result<IReadOnlyList<ManagedInferenceModel>, string>.AsValue(collected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<IReadOnlyList<ManagedInferenceModel>, string>.AsError(
                "OpenRouter GET /models request was cancelled.");
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<ManagedInferenceModel>, string>.AsError(
                $"OpenRouter GET /models failed: {ex.Message}", ex);
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
                DysonManagedSources.OpenRouter,
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
                DysonManagedSources.OpenRouter,
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

    internal static OpenRouterModelsPage ParseModelsPage(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement;
        var models = new List<ManagedInferenceModel>();
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var mapped = MapModel(item);
                if (mapped is not null)
                    models.Add(mapped);
            }
        }

        string? next = null;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("links", out var links)
            && links.ValueKind == JsonValueKind.Object
            && links.TryGetProperty("next", out var nextEl)
            && nextEl.ValueKind == JsonValueKind.String)
        {
            var raw = nextEl.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
                next = raw.Trim();
        }

        return new OpenRouterModelsPage(models, next);
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
        var display = slug;
        if (item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
        {
            var name = nameEl.GetString();
            if (!string.IsNullOrWhiteSpace(name))
                display = name.Trim();
        }

        var modes = MapEffortLevels(item);
        var defaultEffort = ResolveDefaultEffort(ReadDefaultEffort(item), modes);
        return new ManagedInferenceModel(slug, display, defaultEffort, modes);
    }

    internal static string ResolveNextUrl(string currentUrl, string next)
    {
        if (Uri.TryCreate(next, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        if (Uri.TryCreate(currentUrl, UriKind.Absolute, out var current)
            && Uri.TryCreate(current, next, out var resolved))
        {
            return resolved.ToString();
        }

        return next;
    }

    private static IReadOnlyList<string> MapEffortLevels(JsonElement item)
    {
        IReadOnlyList<string> modes;
        var mandatory = false;

        if (item.TryGetProperty("reasoning", out var reasoning)
            && reasoning.ValueKind == JsonValueKind.Object)
        {
            mandatory = reasoning.TryGetProperty("mandatory", out var manEl)
                && manEl.ValueKind == JsonValueKind.True;

            if (reasoning.TryGetProperty("supported_efforts", out var effortsEl))
            {
                if (effortsEl.ValueKind == JsonValueKind.Null)
                    modes = GatewayEffortLevels;
                else if (effortsEl.ValueKind == JsonValueKind.Array)
                    modes = ReadStringArray(effortsEl);
                else
                    modes = [];
            }
            else
            {
                modes = [];
            }
        }
        else if (SupportsReasoningParameter(item))
        {
            modes = GatewayEffortLevels;
        }
        else
        {
            modes = [];
        }

        return ApplyMandatory(modes, mandatory);
    }

    private static string? ReadDefaultEffort(JsonElement item)
    {
        if (!item.TryGetProperty("reasoning", out var reasoning)
            || reasoning.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!reasoning.TryGetProperty("default_effort", out var defEl)
            || defEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = defEl.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? ResolveDefaultEffort(string? specified, IReadOnlyList<string> modes)
    {
        if (!string.IsNullOrWhiteSpace(specified))
            return specified.Trim();

        foreach (var mode in modes)
        {
            if (string.Equals(mode, "high", StringComparison.OrdinalIgnoreCase))
                return mode;
        }

        return modes.Count > 0 ? modes[0] : null;
    }

    private static IReadOnlyList<string> ApplyMandatory(IReadOnlyList<string> modes, bool mandatory)
    {
        if (!mandatory)
            return modes;

        return modes
            .Where(m => !string.Equals(m, "none", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static bool SupportsReasoningParameter(JsonElement item)
    {
        if (!item.TryGetProperty("supported_parameters", out var parameters)
            || parameters.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var el in parameters.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String)
                continue;

            var value = el.GetString();
            if (string.Equals(value, "reasoning", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "reasoning_effort", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement array)
    {
        var list = new List<string>();
        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String)
                continue;

            var value = el.GetString();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            list.Add(value.Trim());
        }

        return list;
    }
}

internal sealed record OpenRouterModelsPage(
    IReadOnlyList<ManagedInferenceModel> Models,
    string? NextLink);
