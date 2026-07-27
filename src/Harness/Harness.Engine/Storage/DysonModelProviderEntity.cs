namespace DysonHarness;

public sealed class DysonModelProviderEntity
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string ProviderKind { get; set; } = "";
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>OpenAICompatible only. Completions = default.</summary>
    public string OpenAiApiMode { get; set; } = DysonOpenAiApiModes.Completions;
    /// <summary>
    /// When set (e.g. <c>cliproxy-codex</c>), the provider is managed — view-only in UI;
    /// mutate APIs reject edits. Null = user-owned manual provider.
    /// </summary>
    public string? ManagedSource { get; set; }
    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }
    /// <summary>UTC.</summary>
    public DateTime UpdatedUtc { get; set; }

    public List<DysonModelSlugEntity> Slugs { get; set; } = [];
}
