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
        DefaultMaxTargetContextTokens = slug?.DefaultMaxTargetContextTokens;
    }

    /// <summary>Convenience: slug must include <see cref="DysonModelSlugEntity.Provider"/>.</summary>
    public DemoDysonAgentProvider(DysonModelSlugEntity? slug, string? reasoningEffort = null)
        : this(slug?.Provider, slug, reasoningEffort)
    {
    }

    /// <summary>Copy identity from <paramref name="source"/> with an explicit effort (no slug-default fallback).</summary>
    public DemoDysonAgentProvider WithReasoningEffort(string? reasoningEffort)
    {
        return new DemoDysonAgentProvider(this, reasoningEffort);
    }

    private DemoDysonAgentProvider(DemoDysonAgentProvider source, string? reasoningEffort)
    {
        ArgumentNullException.ThrowIfNull(source);
        ProviderId = source.ProviderId;
        SlugId = source.SlugId;
        ApiKey = source.ApiKey;
        Slug = source.Slug;
        DisplayAlias = source.DisplayAlias;
        ProviderKind = source.ProviderKind;
        BaseUrl = source.BaseUrl;
        ProviderDisplayName = source.ProviderDisplayName;
        ReasoningEffort = OpenAiCompatibleAgentProvider.NormalizeReasoningEffort(reasoningEffort);
        DefaultMaxTargetContextTokens = source.DefaultMaxTargetContextTokens;
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
    /// <summary>Slug default max target context; null = harness 100K.</summary>
    public int? DefaultMaxTargetContextTokens { get; }
}
