using System.Text.Json;

namespace DysonHarness;

/// <summary>GET <c>/models</c> for a user-supplied OpenAI-compatible base URL.</summary>
public static class OpenAiCompatibleModelDiscovery
{
    public static string ModelsUrl(string baseUrl) =>
        OpenAiCompatibleHttp.NormalizeBaseUrl(baseUrl).TrimEnd('/') + "/models";

    public static async Task<Result<IReadOnlyList<ManagedSlugSpec>, string>> ListAsync(
        HttpClient http,
        string? baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return Result<IReadOnlyList<ManagedSlugSpec>, string>.AsError(
                "Base URL is required to refresh models.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelsUrl(baseUrl));
            OpenAiCompatibleHttp.ApplyBearerAuth(request, apiKey);

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = text.Length > 600 ? text[..600] + "…" : text;
                return Result<IReadOnlyList<ManagedSlugSpec>, string>.AsError(
                    $"GET /models {(int)response.StatusCode}: {snippet}");
            }

            IReadOnlyList<ManagedModelInfo> parsed;
            try
            {
                parsed = ManagedInferenceProviderBase.ParseModelsPayload(text);
            }
            catch (JsonException)
            {
                return Result<IReadOnlyList<ManagedSlugSpec>, string>.AsError("No models in response.");
            }

            var mapped = ManagedInferenceProviderBase.MapModelsToSlugs(parsed, Array.Empty<string>());
            if (mapped.Count == 0)
                return Result<IReadOnlyList<ManagedSlugSpec>, string>.AsError("No models in response.");

            return Result<IReadOnlyList<ManagedSlugSpec>, string>.AsValue(mapped);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<IReadOnlyList<ManagedSlugSpec>, string>.AsError("GET /models was cancelled.");
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<ManagedSlugSpec>, string>.AsError(
                $"GET /models failed: {ex.Message}", ex);
        }
    }
}
