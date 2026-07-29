namespace DysonHarness;

/// <summary>
/// Subject-owned configured shells.
/// Visibility: current subject only; cross-subject get-by-id → error.
/// </summary>
public interface IDysonConfiguredShellRepository
{
    /// <summary>
    /// When the table has no rows for the current subject, seeds platform defaults
    /// (Windows: Pwsh / PowerShell / Cmd). Other platforms seed nothing until Bash/Zsh runners exist.
    /// </summary>
    Task<VoidResult<string>> EnsureDefaultsAsync(CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DysonConfiguredShellEntity>, string>> ListAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Enabled shells as session specs, ordered by <see cref="DysonConfiguredShellEntity.SortOrder"/>.</summary>
    Task<Result<IReadOnlyList<DysonConfiguredShellSpec>, string>> ListEnabledSpecsAsync(
        CancellationToken cancellationToken = default);

    Task<Result<Guid, string>> CreateAsync(
        string name,
        string executablePath,
        bool isEnabled = true,
        IReadOnlyList<string>? fixedArgs = null,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> UpdateAsync(
        Guid id,
        string name,
        string executablePath,
        bool isEnabled,
        IReadOnlyList<string>? fixedArgs = null,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
