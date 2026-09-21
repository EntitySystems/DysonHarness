using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonPlanRepository(
    DysonDbAccessor accessor,
    IDysonSubjectContext subjectContext) : IDysonPlanRepository
{
    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private readonly IDysonSubjectContext _subjectContext =
        subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));

    public Task<Result<IReadOnlyList<DysonPlan>, string>> ListAsync(
        Guid workDirectoryId,
        CancellationToken cancellationToken = default)
    {
        if (workDirectoryId == Guid.Empty)
            return Task.FromResult(Result<IReadOnlyList<DysonPlan>, string>.AsError("Work directory id is required."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => ListCoreAsync(db, subjectId, workDirectoryId, ct),
            cancellationToken);
    }

    public Task<Result<DysonPlan, string>> GetAsync(
        long planId,
        Guid workDirectoryId,
        CancellationToken cancellationToken = default)
    {
        var precondition = ValidateLookup(planId, workDirectoryId);
        if (precondition.IsError)
            return Task.FromResult(Result<DysonPlan, string>.AsError(precondition.Error));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => GetCoreAsync(db, subjectId, planId, workDirectoryId, ct),
            cancellationToken);
    }

    public Task<Result<long, string>> CreateAsync(
        Guid workDirectoryId,
        DysonPlanKind kind,
        string title,
        string? markdown,
        string? planRelativePath,
        DysonPlanStatus status = DysonPlanStatus.Draft,
        string? note = null,
        Guid? buildAgentId = null,
        CancellationToken cancellationToken = default)
    {
        if (workDirectoryId == Guid.Empty)
            return Task.FromResult(Result<long, string>.AsError("Work directory id is required."));

        var trimmedTitle = title?.Trim() ?? "";
        if (trimmedTitle.Length == 0)
            return Task.FromResult(Result<long, string>.AsError("Title is required."));

        if (!Enum.IsDefined(kind))
            return Task.FromResult(Result<long, string>.AsError($"Unknown plan kind '{kind}'."));
        if (!Enum.IsDefined(status))
            return Task.FromResult(Result<long, string>.AsError($"Unknown plan status '{status}'."));

        var normalizedPath = NormalizePlanPath(planRelativePath);
        var invariant = ValidateKindShape(kind, markdown, normalizedPath);
        if (invariant.IsError)
            return Task.FromResult(Result<long, string>.AsError(invariant.Error));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => CreateCoreAsync(
                db,
                subjectId,
                workDirectoryId,
                kind,
                trimmedTitle,
                markdown,
                normalizedPath,
                status,
                note,
                buildAgentId,
                ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> UpdateAsync(
        long planId,
        Guid workDirectoryId,
        string? title = null,
        string? markdown = null,
        DysonPlanStatus? status = null,
        string? note = null,
        Guid? buildAgentId = null,
        CancellationToken cancellationToken = default)
    {
        var precondition = ValidateLookup(planId, workDirectoryId);
        if (precondition.IsError)
            return Task.FromResult(precondition);

        if (status is { } parsedStatus && !Enum.IsDefined(parsedStatus))
            return Task.FromResult(VoidResult<string>.AsError($"Unknown plan status '{parsedStatus}'."));

        string? trimmedTitle = null;
        if (title is not null)
        {
            trimmedTitle = title.Trim();
            if (trimmedTitle.Length == 0)
                return Task.FromResult(VoidResult<string>.AsError("Title is required."));
        }

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => UpdateCoreAsync(
                db,
                subjectId,
                planId,
                workDirectoryId,
                trimmedTitle,
                markdown,
                status,
                note,
                buildAgentId,
                ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> DeleteAsync(
        long planId,
        Guid workDirectoryId,
        CancellationToken cancellationToken = default)
    {
        var precondition = ValidateLookup(planId, workDirectoryId);
        if (precondition.IsError)
            return Task.FromResult(precondition);

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => DeleteCoreAsync(db, subjectId, planId, workDirectoryId, ct),
            cancellationToken);
    }

    private static async Task<Result<IReadOnlyList<DysonPlan>, string>> ListCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid workDirectoryId,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await WorkDirectoryOwnedAsync(db, subjectId, workDirectoryId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return Result<IReadOnlyList<DysonPlan>, string>.AsError($"Work directory '{workDirectoryId}' not found.");

            var rows = await db.Plans
                .AsNoTracking()
                .Where(p => p.WorkDirectoryId == workDirectoryId)
                .OrderByDescending(p => p.UpdatedUtc)
                .ThenByDescending(p => p.Id)
                .Select(p => new DysonPlan
                {
                    Id = p.Id,
                    WorkDirectoryId = p.WorkDirectoryId,
                    Kind = p.Kind,
                    Title = p.Title,
                    PlanRelativePath = p.PlanRelativePath,
                    Status = p.Status,
                    Note = p.Note,
                    BuildAgentId = p.BuildAgentId,
                    CreatedUtc = p.CreatedUtc,
                    UpdatedUtc = p.UpdatedUtc,
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<DysonPlan>, string>.AsValue(rows);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<IReadOnlyList<DysonPlan>, string>.AsError($"Failed to list plans: {ex.Message}");
        }
    }

    private static async Task<Result<DysonPlan, string>> GetCoreAsync(
        DysonDbContext db,
        string subjectId,
        long planId,
        Guid workDirectoryId,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await WorkDirectoryOwnedAsync(db, subjectId, workDirectoryId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return Result<DysonPlan, string>.AsError($"Work directory '{workDirectoryId}' not found.");

            var entity = await db.Plans
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    p => p.Id == planId && p.WorkDirectoryId == workDirectoryId,
                    cancellationToken)
                .ConfigureAwait(false);

            return entity is null
                ? Result<DysonPlan, string>.AsError($"Plan '{planId}' not found.")
                : Result<DysonPlan, string>.AsValue(ToModel(entity));
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<DysonPlan, string>.AsError($"Failed to load plan: {ex.Message}");
        }
    }

    private static async Task<Result<long, string>> CreateCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid workDirectoryId,
        DysonPlanKind kind,
        string title,
        string? markdown,
        string? planRelativePath,
        DysonPlanStatus status,
        string? note,
        Guid? buildAgentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await WorkDirectoryOwnedAsync(db, subjectId, workDirectoryId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return Result<long, string>.AsError($"Work directory '{workDirectoryId}' not found.");

            var now = DateTime.UtcNow;
            var entity = new DysonPlanEntity
            {
                WorkDirectoryId = workDirectoryId,
                Kind = kind,
                Title = title,
                Markdown = kind == DysonPlanKind.ClassicPlan ? null : markdown,
                PlanRelativePath = planRelativePath,
                Status = status,
                Note = note,
                BuildAgentId = buildAgentId,
                CreatedUtc = now,
                UpdatedUtc = now,
            };

            db.Plans.Add(entity);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);

            if (entity.Id <= 0)
                return Result<long, string>.AsError("Failed to allocate plan id.");

            return Result<long, string>.AsValue(entity.Id);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<long, string>.AsError($"Failed to create plan: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> UpdateCoreAsync(
        DysonDbContext db,
        string subjectId,
        long planId,
        Guid workDirectoryId,
        string? title,
        string? markdown,
        DysonPlanStatus? status,
        string? note,
        Guid? buildAgentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await WorkDirectoryOwnedAsync(db, subjectId, workDirectoryId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return VoidResult<string>.AsError($"Work directory '{workDirectoryId}' not found.");

            var entity = await db.Plans
                .FirstOrDefaultAsync(
                    p => p.Id == planId && p.WorkDirectoryId == workDirectoryId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
                return VoidResult<string>.AsError($"Plan '{planId}' not found.");

            if (title is not null)
                entity.Title = title;
            if (markdown is not null)
                entity.Markdown = markdown;
            if (status is not null)
                entity.Status = status.Value;
            if (note is not null)
                entity.Note = note;
            if (buildAgentId is not null)
                entity.BuildAgentId = buildAgentId;

            var invariant = ValidateKindShape(entity.Kind, entity.Markdown, entity.PlanRelativePath);
            if (invariant.IsError)
                return invariant;

            entity.UpdatedUtc = DateTime.UtcNow;
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return VoidResult<string>.AsError($"Failed to update plan: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> DeleteCoreAsync(
        DysonDbContext db,
        string subjectId,
        long planId,
        Guid workDirectoryId,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await WorkDirectoryOwnedAsync(db, subjectId, workDirectoryId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return VoidResult<string>.AsError($"Work directory '{workDirectoryId}' not found.");

            var entity = await db.Plans
                .FirstOrDefaultAsync(
                    p => p.Id == planId && p.WorkDirectoryId == workDirectoryId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
                return VoidResult<string>.AsError($"Plan '{planId}' not found.");

            db.Plans.Remove(entity);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return VoidResult<string>.AsError($"Failed to delete plan: {ex.Message}");
        }
    }

    /// <summary>
    /// MetaPlan ⇒ non-empty Markdown and null PlanRelativePath;
    /// ClassicPlan ⇒ the reverse. One check for every write path.
    /// </summary>
    private static VoidResult<string> ValidateKindShape(
        DysonPlanKind kind,
        string? markdown,
        string? planRelativePath)
    {
        var hasMarkdown = !string.IsNullOrWhiteSpace(markdown);
        var hasPath = !string.IsNullOrWhiteSpace(planRelativePath);

        return kind switch
        {
            DysonPlanKind.MetaPlan when !hasMarkdown =>
                VoidResult<string>.AsError("MetaPlan requires non-empty Markdown."),
            DysonPlanKind.MetaPlan when hasPath =>
                VoidResult<string>.AsError("MetaPlan must not have a PlanRelativePath."),
            DysonPlanKind.ClassicPlan when hasMarkdown =>
                VoidResult<string>.AsError("ClassicPlan must not have Markdown."),
            DysonPlanKind.ClassicPlan when !hasPath =>
                VoidResult<string>.AsError("ClassicPlan requires a PlanRelativePath."),
            DysonPlanKind.MetaPlan or DysonPlanKind.ClassicPlan =>
                VoidResult<string>.Success,
            _ => VoidResult<string>.AsError($"Unknown plan kind '{kind}'."),
        };
    }

    private static VoidResult<string> ValidateLookup(long planId, Guid workDirectoryId)
    {
        if (planId <= 0)
            return VoidResult<string>.AsError("Plan id must be a positive integer.");
        if (workDirectoryId == Guid.Empty)
            return VoidResult<string>.AsError("Work directory id is required.");
        return VoidResult<string>.Success;
    }

    private static async Task<bool> WorkDirectoryOwnedAsync(
        DysonDbContext db,
        string subjectId,
        Guid workDirectoryId,
        CancellationToken cancellationToken) =>
        await db.WorkDirectories
            .AsNoTracking()
            .AnyAsync(w => w.Id == workDirectoryId && w.SubjectId == subjectId, cancellationToken)
            .ConfigureAwait(false);

    private static string? NormalizePlanPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        return path.Trim().Replace('\\', '/');
    }

    private static DysonPlan ToModel(DysonPlanEntity entity) => new()
    {
        Id = entity.Id,
        WorkDirectoryId = entity.WorkDirectoryId,
        Kind = entity.Kind,
        Title = entity.Title,
        Markdown = entity.Markdown,
        PlanRelativePath = entity.PlanRelativePath,
        Status = entity.Status,
        Note = entity.Note,
        BuildAgentId = entity.BuildAgentId,
        CreatedUtc = entity.CreatedUtc,
        UpdatedUtc = entity.UpdatedUtc,
    };
}
