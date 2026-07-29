namespace DysonHarness;

public sealed class DysonModelFavoriteEntity
{
    public Guid Id { get; set; }

    /// <summary>Owning subject (favorites stay subject-owned even for shared-provider slugs).</summary>
    public string SubjectId { get; set; } = "";

    public Guid ModelSlugId { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }

    public DysonModelSlugEntity? ModelSlug { get; set; }
}
