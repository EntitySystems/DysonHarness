namespace DysonHarness;

/// <summary>
/// Secrets for one root Meta session, current subject only. A session that does not belong to the
/// current subject is an error. Error strings never contain a secret value.
/// </summary>
public interface IDysonSessionSecretRepository
{
    /// <summary>Insert or overwrite <c>(sessionId, key)</c>. Key/description are trimmed; the secret is stored as given.</summary>
    Task<VoidResult<string>> UpsertAsync(
        Guid sessionId,
        string key,
        string description,
        string secret,
        CancellationToken cancellationToken = default);

    /// <summary>Keys only, ordinal order. Never values.</summary>
    Task<Result<IReadOnlyList<string>, string>> ListKeysAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>The value. Callers must not put it in a turn, a tool result, or a log.</summary>
    Task<Result<string, string>> GetAsync(
        Guid sessionId,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>Hard delete. A missing row is success.</summary>
    Task<VoidResult<string>> DeleteAsync(
        Guid sessionId,
        string key,
        CancellationToken cancellationToken = default);
}
