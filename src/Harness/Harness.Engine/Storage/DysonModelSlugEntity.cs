namespace DysonHarness;

public sealed class DysonModelSlugEntity
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public string Slug { get; set; } = "";
    public string DisplayAlias { get; set; } = "";
    public bool IsDefault { get; set; }
    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }
    /// <summary>UTC.</summary>
    public DateTime UpdatedUtc { get; set; }

    public DysonModelProviderEntity? Provider { get; set; }
}
