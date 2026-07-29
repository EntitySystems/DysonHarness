namespace DysonHarness;

/// <summary>
/// Subject row ensure + subject-scoped key/value settings (replaces public
/// <c>DysonAppSettingsStore</c> for callers). Never ensures <see cref="DysonSubjects.Shared"/>.
/// Visibility: current subject only.
/// </summary>
public interface IDysonSubjectSettingsRepository
{
    /// <summary>Upsert the <c>subjects</c> row for the current context.</summary>
    Task<VoidResult<string>> EnsureSubjectAsync(CancellationToken cancellationToken = default);

    Task<Result<string?, string>> GetSettingAsync(
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a value; null or whitespace deletes the row.</summary>
    Task<VoidResult<string>> SetSettingAsync(
        string key,
        string? value,
        CancellationToken cancellationToken = default);
}
