namespace DysonHarness;

/// <summary>Ephemeral OpenAI-compatible provider built from a model provider + slug.</summary>
public sealed class OpenAiCompatibleAgentProvider : DysonAgentProvider
{
    public OpenAiCompatibleAgentProvider(DysonModelProviderEntity? provider, DysonModelSlugEntity? slug)
    {
        ProviderId = provider?.Id ?? slug?.ProviderId;
        SlugId = slug?.Id;
        ApiKey = provider?.ApiKey ?? slug?.Provider?.ApiKey;
        Slug = slug?.Slug ?? "gpt-4o";
        DisplayAlias = slug?.DisplayAlias ?? Slug;
        ProviderKind = provider?.ProviderKind
            ?? slug?.Provider?.ProviderKind
            ?? DysonProviderKinds.OpenAICompatible;
        BaseUrl = provider?.BaseUrl ?? slug?.Provider?.BaseUrl;
        ProviderDisplayName = provider?.DisplayName
            ?? slug?.Provider?.DisplayName
            ?? "OpenAI Compatible";
        OpenAiApiMode = DysonOpenAiApiModes.Normalize(
            provider?.OpenAiApiMode ?? slug?.Provider?.OpenAiApiMode);
    }

    /// <summary>Convenience: slug must include <see cref="DysonModelSlugEntity.Provider"/>.</summary>
    public OpenAiCompatibleAgentProvider(DysonModelSlugEntity? slug)
        : this(slug?.Provider, slug)
    {
    }

    public Guid? ProviderId { get; }
    public Guid? SlugId { get; }
    public string? ApiKey { get; }
    public string Slug { get; }
    public string DisplayAlias { get; }
    public string ProviderKind { get; }
    public string? BaseUrl { get; }
    public string ProviderDisplayName { get; }
    public string OpenAiApiMode { get; }
}
