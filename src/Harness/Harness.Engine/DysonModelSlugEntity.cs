namespace DysonHarness;

public sealed class DysonModelSlugEntity
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public string Slug { get; set; } = "";
    public string DisplayAlias { get; set; } = "";
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }

    public DysonModelProviderEntity? Provider { get; set; }
}
