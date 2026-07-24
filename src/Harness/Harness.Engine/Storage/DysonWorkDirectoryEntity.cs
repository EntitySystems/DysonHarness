namespace DysonHarness;

public sealed class DysonWorkDirectoryEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Normalized absolute path (unique).</summary>
    public string AbsolutePath { get; set; } = "";
    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }
    /// <summary>UTC.</summary>
    public DateTime LastOpenedUtc { get; set; }

    public List<DysonSessionEntity> Sessions { get; set; } = [];
}
