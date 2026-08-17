using DysonHarness;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// ponytail: process-restart recovery finalizes durable unfinished work without replay.
/// </summary>
public class DysonSessionRecoveryServiceTests
{
    [Fact]
    public async Task RecoverAsync_root_unfinished_turn_is_finalized_without_replay()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var _keepAlive = conn;
        var sessions = DysonTempDb.Sessions(accessor);
        var recovery = new DysonSessionRecoveryService(sessions);

        var rootId = await CreateSessionAsync(sessions, runtimeId: 0, title: "root-unfinished");
        var turnId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        await SeedUnfinishedTurnAsync(sessions, rootId, turnId, assistantText: "partial reply");

        var first = await recovery.RecoverAsync();
        Assert.True(first.IsSuccess, first.IsError ? first.Error : null);
        Assert.Equal(1, first.Value.UnfinishedSessions);
        Assert.Equal(1, first.Value.TurnsRepaired);
        Assert.Equal(0, first.Value.DescendantsInterrupted);

        var full = await sessions.GetFullSessionAsync(rootId);
        Assert.True(full.IsSuccess, full.IsError ? full.Error : null);
        Assert.Equal(DysonSessionStatus.Active, full.Value.Session.Status);

        var persisted = Assert.Single(full.Value.Turns);
        Assert.Equal(turnId, persisted.Id);
        Assert.NotNull(persisted.CompletedUtc);
        Assert.True(
            persisted.CompletedUtc.Value <= DateTime.UtcNow.AddMinutes(1)
            && persisted.CompletedUtc.Value >= DateTime.UtcNow.AddMinutes(-5),
            $"CompletedUtc {persisted.CompletedUtc} is not a current UTC stamp.");
        Assert.Equal(DysonTurnInterruptionReasons.ApplicationRestart, persisted.InterruptionReason);
        Assert.Equal("partial reply", persisted.AssistantText);
        Assert.Equal("scan the tree", persisted.Instruction);
        AssertRepairedToolState(persisted.ToolStateJson);

        var interrupted = Assert.Single(
            full.Value.Logs,
            l => l.Kind == nameof(DysonSessionLogKind.TurnInterrupted));
        var payload = DysonSessionLogPayload.Deserialize<DysonSessionLogTurnInterrupted>(interrupted.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(turnId, payload.TurnId);
        Assert.Equal(DysonTurnInterruptionReasons.ApplicationRestart, payload.Reason);

        Assert.DoesNotContain(
            full.Value.Logs,
            l => l.Kind is nameof(DysonSessionLogKind.AgentReply)
                or nameof(DysonSessionLogKind.Interrupt)
                or nameof(DysonSessionLogKind.SessionStatusChanged));

        var leftover = await sessions.ListActiveSessionsWithUnfinishedTurnsAsync();
        Assert.True(leftover.IsSuccess, leftover.IsError ? leftover.Error : null);
        Assert.Empty(leftover.Value);
    }

    [Fact]
    public async Task RecoverAsync_active_child_is_interrupted_without_parent_report()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var _keepAlive = conn;
        var sessions = DysonTempDb.Sessions(accessor);
        var recovery = new DysonSessionRecoveryService(sessions);

        var rootId = await CreateSessionAsync(sessions, runtimeId: 0, title: "root");
        var childId = await CreateSessionAsync(
            sessions,
            runtimeId: 1,
            title: "child-active",
            parentSessionId: rootId,
            agentMode: DysonAgentModes.Explore);
        var rootTurnId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var childTurnId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        await SeedUnfinishedTurnAsync(sessions, rootId, rootTurnId, assistantText: "root fragment");
        await SeedUnfinishedTurnAsync(sessions, childId, childTurnId, assistantText: "child fragment");

        var recovered = await recovery.RecoverAsync();
        Assert.True(recovered.IsSuccess, recovered.IsError ? recovered.Error : null);
        Assert.Equal(2, recovered.Value.UnfinishedSessions);
        Assert.Equal(2, recovered.Value.TurnsRepaired);
        Assert.Equal(1, recovered.Value.DescendantsInterrupted);

        var fullRoot = await sessions.GetFullSessionAsync(rootId);
        Assert.True(fullRoot.IsSuccess, fullRoot.IsError ? fullRoot.Error : null);
        Assert.Equal(DysonSessionStatus.Active, fullRoot.Value.Session.Status);
        Assert.Equal("root fragment", Assert.Single(fullRoot.Value.Turns).AssistantText);
        Assert.DoesNotContain(
            fullRoot.Value.Logs,
            l => l.Kind is nameof(DysonSessionLogKind.AgentReply)
                or nameof(DysonSessionLogKind.Interrupt)
                or nameof(DysonSessionLogKind.CompletionFlow));
        Assert.Single(fullRoot.Value.Turns);

        var fullChild = await sessions.GetFullSessionAsync(childId);
        Assert.True(fullChild.IsSuccess, fullChild.IsError ? fullChild.Error : null);
        Assert.Equal(DysonSessionStatus.Interrupted, fullChild.Value.Session.Status);
        var childTurn = Assert.Single(fullChild.Value.Turns);
        Assert.NotNull(childTurn.CompletedUtc);
        Assert.Equal(DysonTurnInterruptionReasons.ApplicationRestart, childTurn.InterruptionReason);
        Assert.Equal("child fragment", childTurn.AssistantText);

        var statusLog = Assert.Single(
            fullChild.Value.Logs,
            l => l.Kind == nameof(DysonSessionLogKind.SessionStatusChanged));
        var status = DysonSessionLogPayload.Deserialize<DysonSessionLogSessionStatusChanged>(statusLog.PayloadJson);
        Assert.NotNull(status);
        Assert.Equal(DysonSessionStatus.Interrupted, status.Status);
        Assert.Equal(DysonTurnInterruptionReasons.ApplicationRestart, status.Reason);

        var children = await sessions.ListChildSessionsAsync(rootId);
        Assert.True(children.IsSuccess, children.IsError ? children.Error : null);
        Assert.Equal(DysonSessionStatus.Interrupted, Assert.Single(children.Value).Status);
    }

    [Fact]
    public async Task RecoverAsync_is_idempotent()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var _keepAlive = conn;
        var sessions = DysonTempDb.Sessions(accessor);
        var recovery = new DysonSessionRecoveryService(sessions);

        var rootId = await CreateSessionAsync(sessions, runtimeId: 0, title: "root");
        var childId = await CreateSessionAsync(
            sessions,
            runtimeId: 1,
            title: "child",
            parentSessionId: rootId);
        await SeedUnfinishedTurnAsync(sessions, rootId, Guid.NewGuid(), assistantText: "keep me");
        await SeedUnfinishedTurnAsync(sessions, childId, Guid.NewGuid(), assistantText: "child keep");

        var first = await recovery.RecoverAsync();
        Assert.True(first.IsSuccess, first.IsError ? first.Error : null);
        Assert.Equal(2, first.Value.TurnsRepaired);

        var afterFirstRoot = await sessions.GetFullSessionAsync(rootId);
        var afterFirstChild = await sessions.GetFullSessionAsync(childId);
        Assert.True(afterFirstRoot.IsSuccess, afterFirstRoot.IsError ? afterFirstRoot.Error : null);
        Assert.True(afterFirstChild.IsSuccess, afterFirstChild.IsError ? afterFirstChild.Error : null);
        var completedUtc = afterFirstRoot.Value.Turns[0].CompletedUtc;
        var toolJson = afterFirstRoot.Value.Turns[0].ToolStateJson;

        var second = await recovery.RecoverAsync();
        Assert.True(second.IsSuccess, second.IsError ? second.Error : null);
        Assert.Equal(0, second.Value.UnfinishedSessions);
        Assert.Equal(0, second.Value.TurnsRepaired);
        Assert.Equal(0, second.Value.DescendantsInterrupted);

        var afterSecondRoot = await sessions.GetFullSessionAsync(rootId);
        var afterSecondChild = await sessions.GetFullSessionAsync(childId);
        Assert.True(afterSecondRoot.IsSuccess, afterSecondRoot.IsError ? afterSecondRoot.Error : null);
        Assert.True(afterSecondChild.IsSuccess, afterSecondChild.IsError ? afterSecondChild.Error : null);

        Assert.Equal(DysonSessionStatus.Active, afterSecondRoot.Value.Session.Status);
        Assert.Equal(DysonSessionStatus.Interrupted, afterSecondChild.Value.Session.Status);
        Assert.Equal(completedUtc, afterSecondRoot.Value.Turns[0].CompletedUtc);
        Assert.Equal(toolJson, afterSecondRoot.Value.Turns[0].ToolStateJson);
        Assert.Equal("keep me", afterSecondRoot.Value.Turns[0].AssistantText);
        Assert.Single(
            afterSecondRoot.Value.Logs,
            l => l.Kind == nameof(DysonSessionLogKind.TurnInterrupted));
        Assert.Single(
            afterSecondChild.Value.Logs,
            l => l.Kind == nameof(DysonSessionLogKind.TurnInterrupted));
        Assert.Single(
            afterSecondChild.Value.Logs,
            l => l.Kind == nameof(DysonSessionLogKind.SessionStatusChanged));
    }

    [Fact]
    public async Task RecoverAsync_active_descendant_without_unfinished_turns_is_interrupted()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var _keepAlive = conn;
        var sessions = DysonTempDb.Sessions(accessor);
        var recovery = new DysonSessionRecoveryService(sessions);

        var rootId = await CreateSessionAsync(sessions, runtimeId: 0, title: "root-idle");
        var childId = await CreateSessionAsync(
            sessions,
            runtimeId: 1,
            title: "child-idle",
            parentSessionId: rootId,
            agentMode: DysonAgentModes.Explore);
        var grandchildId = await CreateSessionAsync(
            sessions,
            runtimeId: 2,
            title: "grandchild-empty",
            parentSessionId: childId,
            agentMode: DysonAgentModes.Explore);
        var completedChildId = await CreateSessionAsync(
            sessions,
            runtimeId: 3,
            title: "child-completed",
            parentSessionId: rootId,
            agentMode: DysonAgentModes.Explore,
            status: DysonSessionStatus.Completed);

        var completedTurnId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var completedAt = new DateTime(2026, 8, 16, 18, 0, 0, DateTimeKind.Utc);
        await SeedCompletedTurnAsync(
            sessions,
            childId,
            completedTurnId,
            assistantText: "child already answered",
            completedUtc: completedAt);
        await SeedCompletedTurnAsync(
            sessions,
            completedChildId,
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            assistantText: "done report",
            completedUtc: completedAt);

        var first = await recovery.RecoverAsync();
        Assert.True(first.IsSuccess, first.IsError ? first.Error : null);
        Assert.Equal(0, first.Value.UnfinishedSessions);
        Assert.Equal(0, first.Value.TurnsRepaired);
        Assert.Equal(2, first.Value.DescendantsInterrupted);

        var fullRoot = await sessions.GetFullSessionAsync(rootId);
        Assert.True(fullRoot.IsSuccess, fullRoot.IsError ? fullRoot.Error : null);
        Assert.Equal(DysonSessionStatus.Active, fullRoot.Value.Session.Status);
        Assert.Empty(fullRoot.Value.Turns);
        Assert.DoesNotContain(
            fullRoot.Value.Logs,
            l => l.Kind is nameof(DysonSessionLogKind.AgentReply)
                or nameof(DysonSessionLogKind.Interrupt)
                or nameof(DysonSessionLogKind.CompletionFlow)
                or nameof(DysonSessionLogKind.SessionStatusChanged));

        var fullChild = await sessions.GetFullSessionAsync(childId);
        Assert.True(fullChild.IsSuccess, fullChild.IsError ? fullChild.Error : null);
        Assert.Equal(DysonSessionStatus.Interrupted, fullChild.Value.Session.Status);
        var childTurn = Assert.Single(fullChild.Value.Turns);
        Assert.Equal(completedTurnId, childTurn.Id);
        Assert.Equal(completedAt, childTurn.CompletedUtc);
        Assert.Null(childTurn.InterruptionReason);
        Assert.Equal("child already answered", childTurn.AssistantText);
        Assert.DoesNotContain(
            fullChild.Value.Logs,
            l => l.Kind == nameof(DysonSessionLogKind.TurnInterrupted));
        var childStatus = Assert.Single(
            fullChild.Value.Logs,
            l => l.Kind == nameof(DysonSessionLogKind.SessionStatusChanged));
        var childPayload = DysonSessionLogPayload.Deserialize<DysonSessionLogSessionStatusChanged>(
            childStatus.PayloadJson);
        Assert.NotNull(childPayload);
        Assert.Equal(DysonSessionStatus.Interrupted, childPayload.Status);
        Assert.Equal(DysonTurnInterruptionReasons.ApplicationRestart, childPayload.Reason);

        var fullGrandchild = await sessions.GetFullSessionAsync(grandchildId);
        Assert.True(fullGrandchild.IsSuccess, fullGrandchild.IsError ? fullGrandchild.Error : null);
        Assert.Equal(DysonSessionStatus.Interrupted, fullGrandchild.Value.Session.Status);
        Assert.Empty(fullGrandchild.Value.Turns);
        Assert.Single(
            fullGrandchild.Value.Logs,
            l => l.Kind == nameof(DysonSessionLogKind.SessionStatusChanged));

        var fullCompleted = await sessions.GetFullSessionAsync(completedChildId);
        Assert.True(fullCompleted.IsSuccess, fullCompleted.IsError ? fullCompleted.Error : null);
        Assert.Equal(DysonSessionStatus.Completed, fullCompleted.Value.Session.Status);
        Assert.Empty(fullCompleted.Value.Logs);

        var leftoverDescendants = await sessions.ListActiveDescendantSessionsAsync();
        Assert.True(leftoverDescendants.IsSuccess, leftoverDescendants.IsError ? leftoverDescendants.Error : null);
        Assert.Empty(leftoverDescendants.Value);

        var second = await recovery.RecoverAsync();
        Assert.True(second.IsSuccess, second.IsError ? second.Error : null);
        Assert.Equal(0, second.Value.UnfinishedSessions);
        Assert.Equal(0, second.Value.TurnsRepaired);
        Assert.Equal(0, second.Value.DescendantsInterrupted);

        var afterSecondChild = await sessions.GetFullSessionAsync(childId);
        Assert.True(afterSecondChild.IsSuccess, afterSecondChild.IsError ? afterSecondChild.Error : null);
        Assert.Equal(DysonSessionStatus.Interrupted, afterSecondChild.Value.Session.Status);
        Assert.Equal(completedAt, afterSecondChild.Value.Turns[0].CompletedUtc);
        Assert.Single(
            afterSecondChild.Value.Logs,
            l => l.Kind == nameof(DysonSessionLogKind.SessionStatusChanged));
    }

    [Fact]
    public async Task RecoverAsync_does_not_mutate_assistant_text_or_add_turns()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var _keepAlive = conn;
        var sessions = DysonTempDb.Sessions(accessor);
        var recovery = new DysonSessionRecoveryService(sessions);

        var rootId = await CreateSessionAsync(sessions, runtimeId: 0, title: "root");
        var finishedTurnId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var unfinishedTurnId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        var finished = CreateMixedToolTurn(finishedTurnId, "already done");
        finished.FinalizeIncompleteTools("prior cancel");
        finished.CompletedUtc = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var finishedEntity = DysonTurnPersistence.ToEntity(finished, rootId, sequence: 1);
        var finishedUpsert = await sessions.UpsertTurnAsync(finishedEntity);
        Assert.False(finishedUpsert.IsError, finishedUpsert.IsError ? finishedUpsert.Error : null);

        await SeedUnfinishedTurnAsync(sessions, rootId, unfinishedTurnId, assistantText: "kept fragment");

        var recovered = await recovery.RecoverAsync();
        Assert.True(recovered.IsSuccess, recovered.IsError ? recovered.Error : null);
        Assert.Equal(1, recovered.Value.TurnsRepaired);

        var full = await sessions.GetFullSessionAsync(rootId);
        Assert.True(full.IsSuccess, full.IsError ? full.Error : null);
        Assert.Equal(2, full.Value.Turns.Count);

        var prior = Assert.Single(full.Value.Turns, t => t.Id == finishedTurnId);
        Assert.Equal("already done", prior.AssistantText);
        Assert.Equal(new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc), prior.CompletedUtc);
        Assert.Null(prior.InterruptionReason);

        var repaired = Assert.Single(full.Value.Turns, t => t.Id == unfinishedTurnId);
        Assert.Equal("kept fragment", repaired.AssistantText);
        Assert.Equal("scan the tree", repaired.Instruction);
        Assert.NotNull(repaired.CompletedUtc);
        Assert.Equal(DysonTurnInterruptionReasons.ApplicationRestart, repaired.InterruptionReason);
        AssertRepairedToolState(repaired.ToolStateJson);

        Assert.DoesNotContain(full.Value.Logs, l => l.Kind == nameof(DysonSessionLogKind.AgentReply));
        Assert.Single(full.Value.Logs, l => l.Kind == nameof(DysonSessionLogKind.TurnInterrupted));
    }

    private static async Task<Guid> CreateSessionAsync(
        IDysonSessionRepository sessions,
        int runtimeId,
        string title,
        Guid? parentSessionId = null,
        string? agentMode = null,
        DysonSessionStatus status = DysonSessionStatus.Active)
    {
        var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = runtimeId,
            ParentSessionId = parentSessionId,
            AgentMode = agentMode ?? DysonAgentModes.Work,
            Title = title,
            SystemPromptSnapshot = title,
            Status = status,
        });
        Assert.False(created.IsError, created.IsError ? created.Error : null);
        return created.Value;
    }

    private static async Task SeedCompletedTurnAsync(
        IDysonSessionRepository sessions,
        Guid sessionId,
        Guid turnId,
        string assistantText,
        DateTime completedUtc)
    {
        var live = CreateMixedToolTurn(turnId, assistantText);
        live.FinalizeIncompleteTools("already finished");
        live.CompletedUtc = completedUtc;
        var entity = DysonTurnPersistence.ToEntity(
            live,
            sessionId,
            sequence: 1,
            createdUtc: completedUtc.AddMinutes(-1),
            completedUtc: completedUtc);
        Assert.Equal(completedUtc, entity.CompletedUtc);
        var upsert = await sessions.UpsertTurnAsync(entity);
        Assert.False(upsert.IsError, upsert.IsError ? upsert.Error : null);
    }

    private static async Task SeedUnfinishedTurnAsync(
        IDysonSessionRepository sessions,
        Guid sessionId,
        Guid turnId,
        string assistantText)
    {
        var live = CreateMixedToolTurn(turnId, assistantText);
        var entity = DysonTurnPersistence.ToEntity(live, sessionId, sequence: 2);
        Assert.Null(entity.CompletedUtc);
        var upsert = await sessions.UpsertTurnAsync(entity);
        Assert.False(upsert.IsError, upsert.IsError ? upsert.Error : null);
    }

    private static void AssertRepairedToolState(string toolStateJson)
    {
        var restored = new DysonAgentTurn { Id = Guid.NewGuid() };
        DysonTurnToolStateSerializer.ApplyToTurn(restored, toolStateJson);

        var completed = Assert.Single(restored.TrackedToolCalls, t => t.Call.CallId == "grep-done");
        Assert.Equal(DysonToolCallStatus.Completed, completed.Status);
        Assert.False(completed.Result?.IsError ?? true);
        Assert.Equal("2 hits", completed.Result?.Content);

        var hung = Assert.Single(restored.TrackedToolCalls, t => t.Call.CallId == "shell-hang");
        Assert.Equal(DysonToolCallStatus.Failed, hung.Status);
        Assert.True(hung.Result?.IsError);
        Assert.Equal(DysonSessionRecoveryService.IncompleteToolReason, hung.Result?.Content);
    }

    private static DysonAgentTurn CreateMixedToolTurn(Guid turnId, string assistantText)
    {
        var turn = new DysonAgentTurn
        {
            Id = turnId,
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "scan the tree",
            AssistantText = assistantText,
            StartedUtc = DateTime.UtcNow,
        };
        turn.ToolCalls.Add(new DysonToolCall
        {
            CallId = "grep-done",
            ToolName = "Grep",
            Stage = 0,
            ArgumentsJson = """{"pattern":"foo"}""",
        });
        turn.ToolCalls.Add(new DysonToolCall
        {
            CallId = "shell-hang",
            ToolName = "ShellExecute",
            Stage = 0,
            ArgumentsJson = """{"command":"sleep"}""",
        });
        turn.RestoreTrackedCalls(
        [
            new DysonPersistedTrackedToolCall
            {
                CallId = "grep-done",
                Status = DysonToolCallStatus.Completed,
                Result = new DysonToolCallResult
                {
                    CallId = "grep-done",
                    ToolName = "Grep",
                    Stage = 0,
                    IsError = false,
                    Content = "2 hits",
                },
            },
            new DysonPersistedTrackedToolCall
            {
                CallId = "shell-hang",
                Status = DysonToolCallStatus.Working,
            },
        ]);
        turn.RestoreResponseLog(
        [
            new DysonToolCallResult
            {
                CallId = "grep-done",
                ToolName = "Grep",
                Stage = 0,
                IsError = false,
                Content = "2 hits",
            },
        ]);
        return turn;
    }
}
