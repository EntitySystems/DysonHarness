namespace DysonHarness;

/// <summary>
/// Eligibility checks for the direct OpenAI Images API. Image generation intentionally does not
/// use generic OpenAI-compatible endpoints, managed providers, or the active chat provider.
/// </summary>
public static class OpenAiImageGenerationEligibility
{
    /// <summary>
    /// Returns true only for an enabled slug with credentials for the direct OpenAI v1 endpoint.
    /// </summary>
    public static bool IsEligible(DysonModelSlugEntity? slug) =>
        IsEligible(slug, slug?.Provider);

    /// <summary>
    /// Returns true only for an enabled slug and its provider when listed separately by a repository.
    /// </summary>
    public static bool IsEligible(DysonModelSlugEntity? slug, DysonModelProviderEntity? modelProvider)
    {
        if (slug is null || !slug.IsEnabled || string.IsNullOrWhiteSpace(slug.Slug))
            return false;

        return IsEligible(new OpenAiCompatibleAgentProvider(modelProvider, slug));
    }

    /// <summary>
    /// Returns true only when <paramref name="provider"/> can call OpenAI's direct Images API.
    /// </summary>
    public static bool IsEligible(OpenAiCompatibleAgentProvider? provider)
    {
        if (provider is null
            || string.IsNullOrWhiteSpace(provider.Slug)
            || string.IsNullOrWhiteSpace(provider.ApiKey)
            || !string.IsNullOrWhiteSpace(provider.ManagedSource)
            || !string.Equals(
                DysonProviderKinds.EffectiveKind(provider.ProviderKind, provider.BaseUrl, provider.ApiKey),
                DysonProviderKinds.OpenAICompatible,
                StringComparison.Ordinal))
        {
            return false;
        }

        var baseUrl = OpenAiCompatibleHttp.NormalizeBaseUrl(provider.BaseUrl);
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase)
            && (uri.IsDefaultPort || uri.Port == 443)
            && string.Equals(uri.AbsolutePath.TrimEnd('/'), "/v1", StringComparison.OrdinalIgnoreCase);
    }
}
