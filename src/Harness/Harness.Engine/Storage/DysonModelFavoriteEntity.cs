namespace DysonHarness;

public sealed class DysonModelFavoriteEntity
{
    public Guid Id { get; set; }
    public Guid ModelSlugId { get; set; }
    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }

    public DysonModelSlugEntity? ModelSlug { get; set; }
}
