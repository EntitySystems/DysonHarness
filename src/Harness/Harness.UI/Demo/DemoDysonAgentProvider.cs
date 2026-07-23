using DysonHarness;

namespace Harness.UI.Demo;

/// <summary>Ephemeral demo provider built from a model provider + slug (no live API calls).</summary>
public sealed class DemoDysonAgentProvider : DysonAgentProvider
{
    public DemoDysonAgentProvider(DysonModelProviderEntity? provider, DysonModelSlugEntity? slug)
    {
        ProviderId = provider?.Id ?? slug?.ProviderId;
        SlugId = slug?.Id;
        ApiKey = provider?.ApiKey ?? slug?.Provider?.ApiKey;
        Slug = slug?.Slug ?? "demo-mock";
        DisplayAlias = slug?.DisplayAlias ?? "Demo (no slug)";
        ProviderKind = provider?.ProviderKind ?? slug?.Provider?.ProviderKind ?? "demo";
        BaseUrl = provider?.BaseUrl ?? slug?.Provider?.BaseUrl;
        ProviderDisplayName = provider?.DisplayName ?? slug?.Provider?.DisplayName ?? "Demo";
    }

    /// <summary>Convenience: slug must include <see cref="DysonModelSlugEntity.Provider"/>.</summary>
    public DemoDysonAgentProvider(DysonModelSlugEntity? slug)
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
}
