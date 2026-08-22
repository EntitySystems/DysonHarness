namespace DysonHarness;

/// <summary>
/// Append-only usage row for one successful OpenAI-compatible Completions/Responses round.
/// Denormalized strings (no FKs) so analytics survive workdir/session delete.
/// </summary>
public sealed class DysonUsageRequestEntity
{
    public Guid Id { get; set; }

    /// <summary>Owning subject.</summary>
    public string SubjectId { get; set; } = "";

    /// <summary>Snapshot of <see cref="DysonWorkDirectoryEntity.Name"/> at request time.</summary>
    public string WorkDirectoryName { get; set; } = "";

    /// <summary>Persistence id of the session that issued the round.</summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// Persistence id of that session’s root (equals <see cref="SessionId"/> on roots).
    /// Recap query key for root + descendants.
    /// </summary>
    public Guid RootSessionId { get; set; }

    /// <summary>API slug at request time.</summary>
    public string ModelSlug { get; set; } = "";

    /// <summary>UI alias snapshot (fallback to slug if empty).</summary>
    public string ModelDisplayAlias { get; set; } = "";

    /// <summary>Reasoning effort snapshot; empty string when omit.</summary>
    public string ReasoningEffort { get; set; } = "";

    /// <summary>UTC.</summary>
    public DateTime OccurredUtc { get; set; }

    /// <summary>Provider <c>prompt_tokens</c> / <c>input_tokens</c>. Missing → 0.</summary>
    public int InputTokens { get; set; }

    /// <summary>Provider cache-read tokens. Missing → 0.</summary>
    public int CacheTokens { get; set; }

    /// <summary>Provider <c>completion_tokens</c> / <c>output_tokens</c>. Missing → 0.</summary>
    public int WriteTokens { get; set; }

    /// <summary>Provider cache-write / cache-creation tokens. Missing → 0.</summary>
    public int CacheWriteTokens { get; set; }

    /// <summary><c>max(0, InputTokens - CacheTokens)</c>.</summary>
    public int InputTokensAfterCache { get; set; }

    /// <summary>
    /// <see cref="WriteTokens"/> unless the provider reports output cache, then
    /// <c>max(0, WriteTokens - outputCache)</c>.
    /// </summary>
    public int WriteTokensAfterCache { get; set; }
}
