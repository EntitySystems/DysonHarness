namespace DysonHarness;

public sealed class DysonModelProviderEntity
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string ProviderKind { get; set; } = "";
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }
    /// <summary>UTC.</summary>
    public DateTime UpdatedUtc { get; set; }

    public List<DysonModelSlugEntity> Slugs { get; set; } = [];
}
