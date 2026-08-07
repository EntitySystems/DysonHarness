using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonWorkDirectoryConfigurationRepository(
    DysonDbAccessor accessor,
    IDysonSubjectContext subjectContext) : IDysonWorkDirectoryConfigurationRepository
{
    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private readonly IDysonSubjectContext _subjectContext =
        subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));

    public Task<Result<JsonNode, string>> GetAsync(
        Guid workDirectoryId,
        CancellationToken cancellationToken = default)
    {
        if (workDirectoryId == Guid.Empty)
            return Task.FromResult(Result<JsonNode, string>.AsError("Work directory id is required."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => GetCoreAsync(db, subjectId, workDirectoryId, ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> UpsertAsync(
        Guid workDirectoryId,
        JsonNode config,
        CancellationToken cancellationToken = default)
    {
        if (workDirectoryId == Guid.Empty)
            return Task.FromResult(new VoidResult<string>("Work directory id is required."));
        ArgumentNullException.ThrowIfNull(config);

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => UpsertCoreAsync(db, subjectId, workDirectoryId, config, ct),
            cancellationToken);
    }

    private static async Task<Result<JsonNode, string>> GetCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid workDirectoryId,
        CancellationToken cancellationToken)
    {
        try
        {
            var workDir = await db.WorkDirectories
                .AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == workDirectoryId && w.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);

            if (workDir is null)
                return Result<JsonNode, string>.AsError($"Work directory '{workDirectoryId}' not found.");

            var row = await db.WorkDirectoryConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    c => c.WorkDirectoryId == workDirectoryId && c.SubjectId == subjectId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
                return Result<JsonNode, string>.AsValue(DysonWorkDirectoryConfig.CreateDefault());

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(row.ConfigJson);
            }
            catch (Exception ex)
            {
                return Result<JsonNode, string>.AsError($"Invalid work directory config JSON: {ex.Message}");
            }

            return Result<JsonNode, string>.AsValue(parsed ?? DysonWorkDirectoryConfig.CreateDefault());
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<JsonNode, string>.AsError($"Failed to load work directory config: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> UpsertCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid workDirectoryId,
        JsonNode config,
        CancellationToken cancellationToken)
    {
        try
        {
            var workDir = await db.WorkDirectories
                .AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == workDirectoryId && w.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);

            if (workDir is null)
                return new VoidResult<string>($"Work directory '{workDirectoryId}' not found.");

            var json = config.ToJsonString();
            var now = DateTime.UtcNow;

            var existing = await db.WorkDirectoryConfigurations
                .FirstOrDefaultAsync(
                    c => c.WorkDirectoryId == workDirectoryId && c.SubjectId == subjectId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                db.WorkDirectoryConfigurations.Add(new DysonWorkDirectoryConfigurationEntity
                {
                    WorkDirectoryId = workDirectoryId,
                    SubjectId = subjectId,
                    ConfigJson = json,
                    UpdatedUtc = now,
                });
            }
            else
            {
                existing.ConfigJson = json;
                existing.UpdatedUtc = now;
            }

            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return new VoidResult<string>($"Failed to save work directory config: {ex.Message}");
        }
    }
}
