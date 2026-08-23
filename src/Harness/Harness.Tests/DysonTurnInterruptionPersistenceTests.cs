using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: durable interruption schema, mapping, and subject-filtered unfinished-turn query.
/// </summary>
public class DysonTurnInterruptionPersistenceTests
{
    [Fact]
    public void Run()
    {
        AssertAppendOnlyDiscriminators();
        AssertTurnPersistenceMapping();
        AssertInterruptedIsTerminal();
        AssertUnfinishedWorkQueryAndSubjectFilter().GetAwaiter().GetResult();
        AssertActiveDescendantQueryAndSubjectFilter().GetAwaiter().GetResult();
    }

    private static void AssertAppendOnlyDiscriminators()
    {
        if ((int)DysonSessionStatus.Active != 0
            || (int)DysonSessionStatus.Completed != 1
            || (int)DysonSessionStatus.Stopped != 2
            || (int)DysonSessionStatus.Failed != 3
            || (int)DysonSessionStatus.Interrupted != 4)
        {
            throw new InvalidOperationException(
                "DysonSessionStatus must stay Active=0 Completed=1 Stopped=2 Failed=3 Interrupted=4.");
        }

        if ((int)DysonSessionLogKind.SessionRenamed != 15
            || (int)DysonSessionLogKind.TurnInterrupted != 16
            || (int)DysonSessionLogKind.Interrupt != 11)
        {
            throw new InvalidOperationException(
                "DysonSessionLogKind must stay Interrupt=11 SessionRenamed=15 TurnInterrupted=16.");
        }

        if (DysonTurnInterruptionReasons.ApplicationRestart != "application-restart")
            throw new InvalidOperationException("ApplicationRestart reason code must stay 'application-restart'.");
    }

    private static void AssertTurnPersistenceMapping()
    {
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "do work",
            AssistantText = "partial",
            InterruptionReason = DysonTurnInterruptionReasons.ApplicationRestart,
            StartedUtc = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc),
            CompletedUtc = new DateTime(2026, 8, 16, 12, 1, 0, DateTimeKind.Utc),
        };

        var sessionId = Guid.NewGuid();
        var entity = DysonTurnPersistence.ToEntity(turn, sessionId, sequence: 2);
        if (entity.InterruptionReason != DysonTurnInterruptionReasons.ApplicationRestart
            || entity.SessionId != sessionId
            || entity.Sequence != 2
            || entity.AssistantText != "partial")
        {
            throw new InvalidOperationException("ToEntity lost InterruptionReason or turn fields.");
        }

        var log = DysonTurnPersistence.CreateTurnInterruptedLog(sessionId, turn);
        if (!DysonSessionLogPayload.TryParseKind(log.Kind, out var kind)
            || kind != DysonSessionLogKind.TurnInterrupted)
        {
            throw new InvalidOperationException($"Expected TurnInterrupted log kind, got '{log.Kind}'.");
        }

        var payload = DysonSessionLogPayload.Deserialize<DysonSessionLogTurnInterrupted>(log.PayloadJson);
        if (payload is null
            || payload.TurnId != turn.Id
            || payload.Reason != DysonTurnInterruptionReasons.ApplicationRestart)
        {
            throw new InvalidOperationException("TurnInterrupted payload mismatch.");
        }

        var unmarked = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal };
        try
        {
            DysonTurnPersistence.CreateTurnInterruptedLog(sessionId, unmarked);
            throw new InvalidOperationException("CreateTurnInterruptedLog must require a reason.");
        }
        catch (ArgumentException)
        {
        }
    }

    private static void AssertInterruptedIsTerminal()
    {
        var session = new StubSession();
        if (session.IsTerminal)
            throw new InvalidOperationException("Fresh session must not be terminal.");

        if (!session.TryMarkTerminal(DysonSessionStatus.Interrupted, "process restart"))
            throw new InvalidOperationException("TryMarkTerminal(Interrupted) must succeed.");

        if (!session.IsTerminal || session.Status != DysonSessionStatus.Interrupted)
            throw new InvalidOperationException("Interrupted must be a terminal session status.");

        if (session.TryAcceptSubagentReport(DysonSessionStatus.Completed, "late report"))
            throw new InvalidOperationException("Interrupted must not be superseded by a subagent report.");
    }

    private static async Task AssertUnfinishedWorkQueryAndSubjectFilter()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var subject = DysonTempDb.Subject("subject-a");
        var sessions = DysonTempDb.Sessions(accessor, subject);

        var activeUnfinished = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 0,
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "root",
            Title = "unfinished-root",
        }).ConfigureAwait(false);
        if (activeUnfinished.IsError)
            throw new InvalidOperationException(activeUnfinished.Error);

        var activeFinished = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 2,
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "done-turn",
        }).ConfigureAwait(false);
        if (activeFinished.IsError)
            throw new InvalidOperationException(activeFinished.Error);

        var completedUnfinished = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 3,
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "completed-session",
            Status = DysonSessionStatus.Completed,
        }).ConfigureAwait(false);
        if (completedUnfinished.IsError)
            throw new InvalidOperationException(completedUnfinished.Error);

        var interruptedUnfinished = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 4,
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "interrupted-session",
            Status = DysonSessionStatus.Interrupted,
        }).ConfigureAwait(false);
        if (interruptedUnfinished.IsError)
            throw new InvalidOperationException(interruptedUnfinished.Error);

        var unfinishedTurnId = Guid.NewGuid();
        var upsertUnfinished = await sessions.UpsertTurnAsync(new DysonTurnEntity
        {
            Id = unfinishedTurnId,
            SessionId = activeUnfinished.Value,
            Sequence = 1,
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "still going",
            ToolStateJson = "{}",
        }).ConfigureAwait(false);
        if (upsertUnfinished.IsError)
            throw new InvalidOperationException(upsertUnfinished.Error);

        var upsertFinished = await sessions.UpsertTurnAsync(new DysonTurnEntity
        {
            SessionId = activeFinished.Value,
            Sequence = 1,
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "done",
            ToolStateJson = "{}",
            CompletedUtc = DateTime.UtcNow,
        }).ConfigureAwait(false);
        if (upsertFinished.IsError)
            throw new InvalidOperationException(upsertFinished.Error);

        var upsertCompletedSessionTurn = await sessions.UpsertTurnAsync(new DysonTurnEntity
        {
            SessionId = completedUnfinished.Value,
            Sequence = 1,
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "orphan unfinished",
            ToolStateJson = "{}",
        }).ConfigureAwait(false);
        if (upsertCompletedSessionTurn.IsError)
            throw new InvalidOperationException(upsertCompletedSessionTurn.Error);

        var upsertInterruptedSessionTurn = await sessions.UpsertTurnAsync(new DysonTurnEntity
        {
            SessionId = interruptedUnfinished.Value,
            Sequence = 1,
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "child interrupted",
            ToolStateJson = "{}",
        }).ConfigureAwait(false);
        if (upsertInterruptedSessionTurn.IsError)
            throw new InvalidOperationException(upsertInterruptedSessionTurn.Error);

        var marked = await sessions.UpsertTurnAsync(new DysonTurnEntity
        {
            Id = unfinishedTurnId,
            SessionId = activeUnfinished.Value,
            Sequence = 1,
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "still going",
            AssistantText = "partial",
            ToolStateJson = "{}",
            InterruptionReason = DysonTurnInterruptionReasons.ApplicationRestart,
        }).ConfigureAwait(false);
        if (marked.IsError)
            throw new InvalidOperationException(marked.Error);

        var full = await sessions.GetFullSessionAsync(activeUnfinished.Value).ConfigureAwait(false);
        if (full.IsError)
            throw new InvalidOperationException(full.Error);
        if (full.Value.Turns.Count != 1
            || full.Value.Turns[0].InterruptionReason != DysonTurnInterruptionReasons.ApplicationRestart
            || full.Value.Turns[0].CompletedUtc is not null)
        {
            throw new InvalidOperationException("Upsert/Get must persist InterruptionReason without completing the turn.");
        }

        var listed = await sessions.ListActiveSessionsWithUnfinishedTurnsAsync().ConfigureAwait(false);
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);
        if (listed.Value.Count != 1 || listed.Value[0].SessionId != activeUnfinished.Value)
        {
            throw new InvalidOperationException(
                "Query must return only the Active session with an unfinished turn.");
        }

        var summary = listed.Value[0];
        if (summary.UnfinishedTurns.Count != 1
            || summary.UnfinishedTurns[0].TurnId != unfinishedTurnId
            || summary.UnfinishedTurns[0].InterruptionReason != DysonTurnInterruptionReasons.ApplicationRestart
            || summary.Title != "unfinished-root")
        {
            throw new InvalidOperationException("Unfinished-turn summary fields mismatch.");
        }

        subject.SubjectId = "subject-b";
        var otherSubject = await sessions.ListActiveSessionsWithUnfinishedTurnsAsync().ConfigureAwait(false);
        if (otherSubject.IsError)
            throw new InvalidOperationException(otherSubject.Error);
        if (otherSubject.Value.Count != 0)
            throw new InvalidOperationException("Subject B must not see Subject A unfinished sessions.");

        var crossGet = await sessions.GetFullSessionAsync(activeUnfinished.Value).ConfigureAwait(false);
        if (!crossGet.IsError
            || crossGet.Error.IndexOf("not found", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException("Cross-subject GetFullSessionAsync must error not-found.");
        }
    }

    private static async Task AssertActiveDescendantQueryAndSubjectFilter()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var subject = DysonTempDb.Subject("subject-a");
        var sessions = DysonTempDb.Sessions(accessor, subject);

        var root = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 0,
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "root",
            Title = "active-root",
        }).ConfigureAwait(false);
        if (root.IsError)
            throw new InvalidOperationException(root.Error);

        var activeChild = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 1,
            ParentSessionId = root.Value,
            AgentMode = DysonAgentModes.Explore,
            SystemPromptSnapshot = "child",
            Title = "active-child",
        }).ConfigureAwait(false);
        if (activeChild.IsError)
            throw new InvalidOperationException(activeChild.Error);

        var completedChild = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 2,
            ParentSessionId = root.Value,
            AgentMode = DysonAgentModes.Explore,
            SystemPromptSnapshot = "done",
            Title = "completed-child",
            Status = DysonSessionStatus.Completed,
        }).ConfigureAwait(false);
        if (completedChild.IsError)
            throw new InvalidOperationException(completedChild.Error);

        var grandchild = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 3,
            ParentSessionId = activeChild.Value,
            AgentMode = DysonAgentModes.Explore,
            SystemPromptSnapshot = "grand",
            Title = "active-grandchild",
        }).ConfigureAwait(false);
        if (grandchild.IsError)
            throw new InvalidOperationException(grandchild.Error);

        var listed = await sessions.ListActiveDescendantSessionsAsync().ConfigureAwait(false);
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);
        if (listed.Value.Count != 2
            || listed.Value.Any(s => s.Id == root.Value || s.Id == completedChild.Value)
            || listed.Value.All(s => s.Id != activeChild.Value)
            || listed.Value.All(s => s.Id != grandchild.Value))
        {
            throw new InvalidOperationException(
                "Query must return only Active descendants, never roots or terminal children.");
        }

        subject.SubjectId = "subject-b";
        var otherSubject = await sessions.ListActiveDescendantSessionsAsync().ConfigureAwait(false);
        if (otherSubject.IsError)
            throw new InvalidOperationException(otherSubject.Error);
        if (otherSubject.Value.Count != 0)
            throw new InvalidOperationException("Subject B must not see Subject A active descendants.");
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
            IReadOnlyList<string>? contextFiles = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> LoadFunctionalContextAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
