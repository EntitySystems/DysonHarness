namespace DysonHarness;

public interface IDysonPluginVariableValueRepository
{
    Task<VoidResult<string>> UpsertAsync(
        Guid installationId,
        string variableName,
        byte[] protectedValue,
        CancellationToken cancellationToken = default);

    Task<Result<DysonPluginVariableValueEntity?, string>> GetAsync(
        Guid installationId,
        string variableName,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlySet<string>, string>> ListNamesAsync(
        Guid installationId,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> DeleteAsync(
        Guid installationId,
        string variableName,
        CancellationToken cancellationToken = default);
}
