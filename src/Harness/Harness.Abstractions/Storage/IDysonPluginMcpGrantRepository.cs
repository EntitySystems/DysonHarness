namespace DysonHarness;

/// <summary>Subject-owned explicit runtime grants for managed plugin MCP servers.</summary>
public interface IDysonPluginMcpGrantRepository
{
    Task<VoidResult<string>> UpsertAsync(
        DysonPluginMcpGrantEntity grant,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> RevokeAsync(
        Guid installationId,
        string serverId,
        DateTime revokedUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns global grants plus grants owned by <paramref name="workDirectoryId"/>. Null returns
    /// global grants only. Revoked rows are included so management callers can inspect history.
    /// </summary>
    Task<Result<IReadOnlyList<DysonPluginMcpGrantEntity>, string>> ListAsync(
        Guid? workDirectoryId = null,
        CancellationToken cancellationToken = default);
}
