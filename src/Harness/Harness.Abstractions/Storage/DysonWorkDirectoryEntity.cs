namespace DysonHarness;

public sealed class DysonWorkDirectoryEntity
{
    public Guid Id { get; set; }

    /// <summary>Owning subject.</summary>
    public string SubjectId { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Normalized absolute path (unique per subject).</summary>
    public string AbsolutePath { get; set; } = "";

    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>UTC.</summary>
    public DateTime LastOpenedUtc { get; set; }

    /// <summary>Raw git origin URL, or null if not a git repo / no origin.</summary>
    public string? GitOrigin { get; set; }

    /// <summary>
    /// Classified provider slug (<c>github</c> / <c>gitlab</c> / <c>azure-devops</c> /
    /// <c>cursor-origin</c> / <c>other</c>), or null.
    /// </summary>
    public string? GitProvider { get; set; }

    public List<DysonSessionEntity> Sessions { get; set; } = [];
}
