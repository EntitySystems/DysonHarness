namespace DysonHarness;

/// <summary>
/// EF row for table <c>meta_scratch_files</c>: a DB-backed markdown document scoped to a work directory,
/// shown in the Meta Agent page's Scratch tab. Not the file-based scratch notes (<c>.dyson/scratch/</c>).
/// </summary>
public sealed class DysonMetaScratchFileEntity
{
    /// <summary>Auto-increment PK; the scratch file <c>id</c> every tool uses.</summary>
    public long Id { get; set; }

    public Guid WorkDirectoryId { get; set; }

    /// <summary>Required, trimmed, at most 200 characters.</summary>
    public string Title { get; set; } = "";

    /// <summary>Required; may be empty; at most <see cref="IDysonMetaScratchFileRepository.MaxContentChars"/> characters.</summary>
    public string Content { get; set; } = "";

    /// <summary>User order. Null ⇒ unpinned (sorted after pinned rows by <see cref="UpdatedUtc"/> descending).</summary>
    public int? SortIndex { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>UTC.</summary>
    public DateTime UpdatedUtc { get; set; }

    public DysonWorkDirectoryEntity? WorkDirectory { get; set; }
}

/// <summary>Runtime / UI / MCP mirror of a <c>meta_scratch_files</c> row (non-EF).</summary>
public sealed class DysonMetaScratchFile
{
    public long Id { get; init; }
    public Guid WorkDirectoryId { get; init; }
    public required string Title { get; init; }

    /// <summary>Body. Loaded by <see cref="IDysonMetaScratchFileRepository.GetAsync"/>; null from <see cref="IDysonMetaScratchFileRepository.ListAsync"/>.</summary>
    public string? Content { get; init; }

    /// <summary>Body length in characters; set by List and Get.</summary>
    public int ContentLength { get; init; }

    public int? SortIndex { get; init; }

    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; init; }

    /// <summary>UTC.</summary>
    public DateTime UpdatedUtc { get; init; }
}
