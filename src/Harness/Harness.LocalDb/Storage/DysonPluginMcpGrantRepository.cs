using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonPluginMcpGrantRepository(
    DysonDbAccessor accessor,
    IDysonSubjectContext subjectContext) : IDysonPluginMcpGrantRepository
{
    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private readonly IDysonSubjectContext _subjectContext =
        subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));

    public Task<VoidResult<string>> UpsertAsync(
        DysonPluginMcpGrantEntity grant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (grant.InstallationId == Guid.Empty)
            return Task.FromResult(VoidResult<string>.AsError("Plugin installation id is required."));
        if (string.IsNullOrWhiteSpace(grant.ServerId))
            return Task.FromResult(VoidResult<string>.AsError("Plugin MCP server id is required."));
        if (grant.Capabilities <= 0)
            return Task.FromResult(VoidResult<string>.AsError("Plugin MCP grant capabilities are required."));
        if (string.IsNullOrWhiteSpace(grant.PackageChecksum))
            return Task.FromResult(VoidResult<string>.AsError("Plugin MCP grants require a package checksum."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, ct) =>
        {
            try
            {
                if (!await db.PluginInstallations.AsNoTracking().AnyAsync(
                        installation => installation.Id == grant.InstallationId &&
                                        installation.SubjectId == subjectId,
                        ct).ConfigureAwait(false))
                {
                    return VoidResult<string>.AsError(
                        "Plugin installation not found for the current subject.");
                }

                var serverId = grant.ServerId.Trim();
                var entity = await db.PluginMcpGrants.FirstOrDefaultAsync(
                    row => row.SubjectId == subjectId &&
                           row.InstallationId == grant.InstallationId &&
                           row.ServerId == serverId,
                    ct).ConfigureAwait(false);
                if (entity is null)
                {
                    entity = new DysonPluginMcpGrantEntity
                    {
                        Id = Guid.NewGuid(),
                        SubjectId = subjectId,
                        InstallationId = grant.InstallationId,
                        ServerId = serverId,
                    };
                    db.PluginMcpGrants.Add(entity);
                }

                entity.Capabilities = grant.Capabilities;
                entity.PackageChecksum = grant.PackageChecksum.Trim();
                entity.GrantedUtc = grant.GrantedUtc == default ? DateTime.UtcNow : grant.GrantedUtc;
                entity.RevokedUtc = null;
                await DysonDbAccessor.SaveChangesAsync(db, ct).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return VoidResult<string>.AsError("Failed to save plugin MCP grant.");
            }
        }, cancellationToken);
    }

    public Task<VoidResult<string>> RevokeAsync(
        Guid installationId,
        string serverId,
        DateTime revokedUtc,
        CancellationToken cancellationToken = default)
    {
        if (installationId == Guid.Empty)
            return Task.FromResult(VoidResult<string>.AsError("Plugin installation id is required."));
        if (string.IsNullOrWhiteSpace(serverId))
            return Task.FromResult(VoidResult<string>.AsError("Plugin MCP server id is required."));

        var subjectId = _subjectContext.SubjectId;
        var normalizedServerId = serverId.Trim();
        return _accessor.RunAsync(async (db, ct) =>
        {
            try
            {
                var entity = await db.PluginMcpGrants.FirstOrDefaultAsync(
                    row => row.SubjectId == subjectId &&
                           row.InstallationId == installationId &&
                           row.ServerId == normalizedServerId,
                    ct).ConfigureAwait(false);
                if (entity is null)
                    return VoidResult<string>.AsError("Active plugin MCP grant not found.");

                entity.RevokedUtc = revokedUtc == default ? DateTime.UtcNow : revokedUtc;
                await DysonDbAccessor.SaveChangesAsync(db, ct).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return VoidResult<string>.AsError("Failed to revoke plugin MCP grant.");
            }
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<DysonPluginMcpGrantEntity>, string>> ListAsync(
        Guid? workDirectoryId = null,
        CancellationToken cancellationToken = default)
    {
        if (workDirectoryId == Guid.Empty)
        {
            return Task.FromResult(Result<IReadOnlyList<DysonPluginMcpGrantEntity>, string>.AsError(
                "Work directory id must be non-empty when specified."));
        }

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, ct) =>
        {
            try
            {
                var query = db.PluginMcpGrants.AsNoTracking()
                    .Where(grant => grant.SubjectId == subjectId &&
                        grant.Installation != null &&
                        grant.Installation.SubjectId == subjectId);
                query = workDirectoryId is Guid id
                    ? query.Where(grant =>
                        grant.Installation!.InstallScope == DysonPluginStorageValues.GlobalScope ||
                        (grant.Installation.InstallScope == DysonPluginStorageValues.ProjectScope &&
                         grant.Installation.WorkDirectoryId == id))
                    : query.Where(grant =>
                        grant.Installation!.InstallScope == DysonPluginStorageValues.GlobalScope);

                var rows = await query
                    .OrderBy(grant => grant.InstallationId)
                    .ThenBy(grant => grant.ServerId)
                    .ToListAsync(ct).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    row.GrantedUtc = DateTime.SpecifyKind(row.GrantedUtc, DateTimeKind.Utc);
                    if (row.RevokedUtc is DateTime revoked)
                        row.RevokedUtc = DateTime.SpecifyKind(revoked, DateTimeKind.Utc);
                }

                return Result<IReadOnlyList<DysonPluginMcpGrantEntity>, string>.AsValue(rows);
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<IReadOnlyList<DysonPluginMcpGrantEntity>, string>.AsError(
                    "Failed to list plugin MCP grants.");
            }
        }, cancellationToken);
    }
}
