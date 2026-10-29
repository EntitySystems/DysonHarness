namespace DysonHarness;

/// <summary>
/// Voice sidecar memory (current subject only). One row per sidecar session. A missing row is empty
/// content at version 0 and is not created until the first successful write.
/// </summary>
public interface IDysonVoiceSidecarMemoryRepository
{
    /// <summary>
    /// The row for <paramref name="sessionId"/>, or empty content at version 0 when none exists.
    /// A session that does not belong to the current subject is an error.
    /// </summary>
    Task<Result<DysonVoiceSidecarMemory, string>> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the row (or empty / version 0), runs <paramref name="transform"/> on the content, and writes
    /// the result with compare-and-set on <c>Version</c>. A transform error or a result longer than
    /// <see cref="DysonVoiceSidecarMemoryLimits.MaxChars"/> writes nothing. A version conflict retries
    /// once against the fresh row, then returns an error.
    /// </summary>
    Task<Result<DysonVoiceSidecarMemory, string>> ApplyAsync(
        Guid sessionId,
        Func<string, Result<string, string>> transform,
        CancellationToken cancellationToken = default);
}
