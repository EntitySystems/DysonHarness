namespace DysonHarness;

/// <summary>
/// Per-work-directory configuration document (JSON), subject-owned.
/// Cascade-deleted with the work directory.
/// </summary>
public sealed class DysonWorkDirectoryConfigurationEntity
{
    /// <summary>PK / FK → <c>work_directories.Id</c>.</summary>
    public Guid WorkDirectoryId { get; set; }

    /// <summary>Owning subject (same as the work directory).</summary>
    public string SubjectId { get; set; } = "";

    /// <summary>Serialized <see cref="System.Text.Json.Nodes.JsonNode"/> document (TEXT).</summary>
    public string ConfigJson { get; set; } = "{}";

    /// <summary>UTC.</summary>
    public DateTime UpdatedUtc { get; set; }

    public DysonWorkDirectoryEntity? WorkDirectory { get; set; }
}
