using DysonHarness;

namespace Harness.UI.Demo;

/// <summary>Ephemeral demo provider built from a model provider + slug (no live API calls).</summary>
public sealed class DemoDysonAgentProvider : DysonAgentProvider
{
    public DemoDysonAgentProvider(
        DysonModelProviderEntity? provider,
        DysonModelSlugEntity? slug,
        string? reasoningEffort = null)
    {
        ProviderId = provider?.Id ?? slug?.ProviderId;
        SlugId = slug?.Id;
        ApiKey = provider?.ApiKey ?? slug?.Provider?.ApiKey;
        Slug = slug?.Slug ?? "demo-mock";
        DisplayAlias = slug?.DisplayAlias ?? "Demo (no slug)";
        ProviderKind = provider?.ProviderKind ?? slug?.Provider?.ProviderKind ?? DysonProviderKinds.Demo;
        BaseUrl = provider?.BaseUrl ?? slug?.Provider?.BaseUrl;
        ProviderDisplayName = provider?.DisplayName ?? slug?.Provider?.DisplayName ?? "Demo";
        ReasoningEffort = OpenAiCompatibleAgentProvider.NormalizeReasoningEffort(
            reasoningEffort ?? slug?.DefaultReasoningEffort);
    }

    /// <summary>Convenience: slug must include <see cref="DysonModelSlugEntity.Provider"/>.</summary>
    public DemoDysonAgentProvider(DysonModelSlugEntity? slug, string? reasoningEffort = null)
        : this(slug?.Provider, slug, reasoningEffort)
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
    /// <summary>Carried for parity with OpenAI provider; demo client ignores it.</summary>
    public string? ReasoningEffort { get; set; }
}
