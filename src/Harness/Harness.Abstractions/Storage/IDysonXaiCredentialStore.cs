namespace DysonHarness;

/// <summary>
/// Singleton-safe store for native xAI OAuth credentials (plaintext JSON rows in <c>app_settings</c>,
/// key <see cref="DysonAppSettingKeys.XaiGrokCredentialPrefix"/> + guid). Looked up by credential handle,
/// not by subject, because callers (the HTTP handler) have no subject context. Never log the JSON.
/// </summary>
public interface IDysonXaiCredentialStore
{
    /// <summary>Stored JSON, or <c>null</c> value when there is no row.</summary>
    Task<Result<string?, string>> GetAsync(Guid credentialId, CancellationToken cancellationToken = default);

    /// <summary>Upsert <paramref name="json"/> verbatim. On update the original <c>SubjectId</c> is kept.</summary>
    Task<VoidResult<string>> SaveAsync(
        Guid credentialId,
        string subjectId,
        string json,
        CancellationToken cancellationToken = default);

    /// <summary>Delete the row; a missing row is success.</summary>
    Task<VoidResult<string>> DeleteAsync(Guid credentialId, CancellationToken cancellationToken = default);
}
