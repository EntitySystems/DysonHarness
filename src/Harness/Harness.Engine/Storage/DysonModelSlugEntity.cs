namespace DysonHarness;

public sealed class DysonModelSlugEntity
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public string Slug { get; set; } = "";
    public string DisplayAlias { get; set; } = "";
    public bool IsDefault { get; set; }
    /// <summary>When false, omitted from new selection catalogs (managed providers only).</summary>
    public bool IsEnabled { get; set; } = true;
    /// <summary>Default top-level reasoning_effort for this slug; null/empty = omit.</summary>
    public string? DefaultReasoningEffort { get; set; }
    /// <summary>reasoning_effort values for the composer dropdown; empty = None only.</summary>
    public List<string> ReasoningModes { get; set; } = [];
    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }
    /// <summary>UTC.</summary>
    public DateTime UpdatedUtc { get; set; }

    public DysonModelProviderEntity? Provider { get; set; }
}
