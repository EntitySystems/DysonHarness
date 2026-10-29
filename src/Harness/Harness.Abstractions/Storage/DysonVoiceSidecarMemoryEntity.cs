namespace DysonHarness;

/// <summary>
/// EF row for table <c>voice_sidecar_memory</c>: one private memory document per voice sidecar session.
/// </summary>
public sealed class DysonVoiceSidecarMemoryEntity
{
    public Guid Id { get; set; }

    /// <summary>Owning subject (<see cref="DysonSubjects.Local"/> or cloud subject id).</summary>
    public string SubjectId { get; set; } = "";

    /// <summary>The sidecar session this memory belongs to. Cascade-deleted with the session.</summary>
    public Guid SessionId { get; set; }

    /// <summary>The whole memory. Empty until first written; at most <see cref="DysonVoiceSidecarMemoryLimits.MaxChars"/> characters.</summary>
    public string Content { get; set; } = "";

    /// <summary>Compare-and-set token. Starts at 1 on insert; incremented on every successful write.</summary>
    public int Version { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>UTC.</summary>
    public DateTime UpdatedUtc { get; set; }

    public DysonSessionEntity? Session { get; set; }
}

/// <summary>Runtime mirror of a <c>voice_sidecar_memory</c> row (non-EF). Version 0 means no row exists yet.</summary>
public sealed record DysonVoiceSidecarMemory(Guid SessionId, string Content, int Version, DateTime? UpdatedUtc);

/// <summary>Hard cap for <see cref="DysonVoiceSidecarMemoryEntity.Content"/>.</summary>
public static class DysonVoiceSidecarMemoryLimits
{
    /// <summary>Maximum memory length in characters (<c>string.Length</c>).</summary>
    public const int MaxChars = 16_384;
}
