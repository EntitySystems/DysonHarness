using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonPendingUserQuestionRepository(
    DysonDbAccessor accessor,
    IDysonSubjectContext subjectContext) : IDysonPendingUserQuestionRepository
{
    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private readonly IDysonSubjectContext _subjectContext =
        subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));

    public Task<Result<ulong, string>> CreateAsync(
        DysonPendingUserQuestion question,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);

        if (question.SessionId == Guid.Empty)
            return Task.FromResult(Result<ulong, string>.AsError("Session id is required."));

        var message = question.Message?.Trim() ?? "";
        if (message.Length == 0)
            return Task.FromResult(Result<ulong, string>.AsError("Message is required."));

        var title = question.Question?.Trim() ?? "";
        if (title.Length == 0)
            return Task.FromResult(Result<ulong, string>.AsError("Question is required."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => CreateCoreAsync(db, subjectId, question, message, title, ct),
            cancellationToken);
    }

    public Task<Result<IReadOnlyList<DysonPendingUserQuestion>, string>> ListAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return Task.FromResult(Result<IReadOnlyList<DysonPendingUserQuestion>, string>.AsError(
                "Session id is required."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => ListCoreAsync(db, subjectId, sessionId, ct),
            cancellationToken);
    }

    public Task<Result<int, string>> CountAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return Task.FromResult(Result<int, string>.AsError("Session id is required."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => CountCoreAsync(db, subjectId, sessionId, ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> DeleteAsync(
        ulong id,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return Task.FromResult(VoidResult<string>.AsError("Session id is required."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => DeleteCoreAsync(db, subjectId, id, sessionId, ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> DeleteByQuestionGuidAsync(
        Guid questionGuid,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return Task.FromResult(VoidResult<string>.AsError("Session id is required."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => DeleteByQuestionGuidCoreAsync(db, subjectId, questionGuid, sessionId, ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> UpdateByQuestionGuidAsync(
        Guid questionGuid,
        Guid sessionId,
        string message,
        string question,
        IReadOnlyList<string> choices,
        bool multiSelect,
        bool allowCustomAnswer,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return Task.FromResult(VoidResult<string>.AsError("Session id is required."));

        var trimmedMessage = message?.Trim() ?? "";
        if (trimmedMessage.Length == 0)
            return Task.FromResult(VoidResult<string>.AsError("Message is required."));

        var title = question?.Trim() ?? "";
        if (title.Length == 0)
            return Task.FromResult(VoidResult<string>.AsError("Question is required."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => UpdateByQuestionGuidCoreAsync(
                db,
                subjectId,
                questionGuid,
                sessionId,
                trimmedMessage,
                title,
                choices,
                multiSelect,
                allowCustomAnswer,
                ct),
            cancellationToken);
    }

    private static async Task<Result<ulong, string>> CreateCoreAsync(
        DysonDbContext db,
        string subjectId,
        DysonPendingUserQuestion question,
        string message,
        string title,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await SessionOwnedAsync(db, subjectId, question.SessionId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return Result<ulong, string>.AsError($"Session '{question.SessionId}' not found.");

            // Leave Id unset so ValueGeneratedOnAdd assigns it. EF's temporary key is a negative
            // long that round-trips through the converter, which is why the converter is unchecked.
            var entity = new DysonPendingUserQuestionEntity
            {
                SubjectId = subjectId,
                SessionId = question.SessionId,
                TurnId = question.TurnId,
                QuestionGuid = question.QuestionGuid,
                Message = message,
                Question = title,
                Kind = question.Kind,
                Choices = StringListJsonValueConverter.Normalize(question.Choices),
                MultiSelect = question.MultiSelect,
                AllowCustomAnswer = question.AllowCustomAnswer,
                SubagentId = question.SubagentId,
                EventId = question.EventId,
                CreatedUtc = DateTime.UtcNow,
            };

            db.PendingUserQuestions.Add(entity);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return Result<ulong, string>.AsValue(entity.Id);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<ulong, string>.AsError($"Failed to create pending user question: {ex.Message}");
        }
    }

    private static async Task<Result<IReadOnlyList<DysonPendingUserQuestion>, string>> ListCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await SessionOwnedAsync(db, subjectId, sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return Result<IReadOnlyList<DysonPendingUserQuestion>, string>.AsError(
                    $"Session '{sessionId}' not found.");

            // ponytail: CreatedUtc only in SQL — EF client-evals ulong OrderBy. Sort Id in memory.
            var rows = await db.PendingUserQuestions
                .AsNoTracking()
                .Where(x => x.SubjectId == subjectId && x.SessionId == sessionId)
                .OrderBy(x => x.CreatedUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            rows.Sort(static (a, b) =>
            {
                var byTime = a.CreatedUtc.CompareTo(b.CreatedUtc);
                return byTime != 0 ? byTime : a.Id.CompareTo(b.Id);
            });

            return Result<IReadOnlyList<DysonPendingUserQuestion>, string>.AsValue(
                rows.Select(ToModel).ToList());
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<IReadOnlyList<DysonPendingUserQuestion>, string>.AsError(
                $"Failed to list pending user questions: {ex.Message}");
        }
    }

    private static async Task<Result<int, string>> CountCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await SessionOwnedAsync(db, subjectId, sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return Result<int, string>.AsError($"Session '{sessionId}' not found.");

            var count = await db.PendingUserQuestions
                .AsNoTracking()
                .CountAsync(x => x.SubjectId == subjectId && x.SessionId == sessionId, cancellationToken)
                .ConfigureAwait(false);

            return Result<int, string>.AsValue(count);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<int, string>.AsError($"Failed to count pending user questions: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> DeleteCoreAsync(
        DysonDbContext db,
        string subjectId,
        ulong id,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await SessionOwnedAsync(db, subjectId, sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return VoidResult<string>.AsError($"Session '{sessionId}' not found.");

            await db.PendingUserQuestions
                .Where(x => x.SubjectId == subjectId && x.SessionId == sessionId && x.Id == id)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return VoidResult<string>.AsError($"Failed to delete pending user question: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> DeleteByQuestionGuidCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid questionGuid,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await SessionOwnedAsync(db, subjectId, sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return VoidResult<string>.AsError($"Session '{sessionId}' not found.");

            await db.PendingUserQuestions
                .Where(x => x.SubjectId == subjectId && x.SessionId == sessionId && x.QuestionGuid == questionGuid)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return VoidResult<string>.AsError($"Failed to delete pending user question: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> UpdateByQuestionGuidCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid questionGuid,
        Guid sessionId,
        string message,
        string title,
        IReadOnlyList<string> choices,
        bool multiSelect,
        bool allowCustomAnswer,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await SessionOwnedAsync(db, subjectId, sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return VoidResult<string>.AsError($"Session '{sessionId}' not found.");

            var row = await db.PendingUserQuestions
                .Where(x => x.SubjectId == subjectId && x.SessionId == sessionId && x.QuestionGuid == questionGuid)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
                return VoidResult<string>.AsError("not found.");
            if (row.Kind != DysonPendingUserQuestionKind.Question)
                return VoidResult<string>.AsError("not a question.");

            row.Message = message;
            row.Question = title;
            row.Choices = StringListJsonValueConverter.Normalize(choices);
            row.MultiSelect = multiSelect;
            row.AllowCustomAnswer = allowCustomAnswer;
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return VoidResult<string>.AsError($"Failed to update pending user question: {ex.Message}");
        }
    }

    private static async Task<bool> SessionOwnedAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        CancellationToken cancellationToken) =>
        await db.Sessions
            .AsNoTracking()
            .AnyAsync(s => s.Id == sessionId && s.SubjectId == subjectId, cancellationToken)
            .ConfigureAwait(false);

    private static DysonPendingUserQuestion ToModel(DysonPendingUserQuestionEntity entity) =>
        new(
            entity.Id,
            entity.SessionId,
            entity.TurnId,
            entity.QuestionGuid,
            entity.Message,
            entity.Question,
            entity.Choices,
            entity.MultiSelect,
            entity.AllowCustomAnswer,
            entity.SubagentId,
            entity.EventId,
            entity.CreatedUtc,
            entity.Kind);
}
