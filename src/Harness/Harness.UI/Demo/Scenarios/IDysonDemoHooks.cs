#if DYSON_PREVIEW
using DysonHarness;

namespace Harness.UI.Demo;

public sealed record DysonDemoStart(Guid WorkDirectoryId, Guid SessionId, Guid? PreviousWorkDirectoryId);

public sealed record DysonDemoAgent(Guid PersistenceId, int RuntimeId);

public sealed record DysonDemoQuestion(string Question, IReadOnlyList<string> Choices);

public interface IDysonDemoHooks
{
    Task<Result<DysonDemoStart, string>> StartAsync(string workDirectoryName, CancellationToken ct);

    Task<VoidResult<string>> AppendUserTurnAsync(string instruction, string assistantText, CancellationToken ct);

    Task<Result<Guid?, string>> AppendAssistantMessageAsync(string message, DysonDemoQuestion? question, CancellationToken ct);

    Task<VoidResult<string>> AnswerQuestionAsync(Guid questionId, string selectedChoice, CancellationToken ct);

    Task<Result<long, string>> CreatePlanAsync(
        string title,
        string markdown,
        CancellationToken ct,
        DysonPlanStatus status = DysonPlanStatus.Draft,
        DateTime? updatedUtc = null,
        bool archived = false);

    Task<VoidResult<string>> OpenPlanAsync(long planId, CancellationToken ct);

    Task<VoidResult<string>> SetPlanStatusAsync(long planId, DysonPlanStatus status, CancellationToken ct);

    Task<Result<DysonDemoAgent, string>> AddAgentAsync(Guid? parentPersistenceId, string title, string mode, CancellationToken ct);

    Task<VoidResult<string>> SetAgentStatusAsync(Guid persistenceId, DysonSessionStatus status, string? activity, CancellationToken ct);

    Task<VoidResult<string>> CleanupAsync(CancellationToken ct);
}
#endif
