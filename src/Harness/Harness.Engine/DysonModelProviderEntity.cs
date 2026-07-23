namespace DysonHarness;

public sealed class DysonModelProviderEntity
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string ProviderKind { get; set; } = "";
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }

    public List<DysonModelSlugEntity> Slugs { get; set; } = [];
}
