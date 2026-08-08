namespace DysonHarness;

public interface IDysonPluginHookSecurityRepository
{
    Task<VoidResult<string>> UpsertReviewAsync(
        DysonPluginHookReviewEntity review,
        CancellationToken cancellationToken = default);

    Task<Result<DysonPluginHookReviewEntity?, string>> GetReviewAsync(
        Guid installationId,
        string hookComponentId,
        string eventName,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> RevokeReviewAsync(
        Guid installationId,
        string hookComponentId,
        string eventName,
        DateTime revokedUtc,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> AppendAuditAsync(
        DysonPluginHookAuditEntity audit,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DysonPluginHookAuditEntity>, string>> ListAuditAsync(
        Guid installationId,
        CancellationToken cancellationToken = default);
}
