using DysonHarness;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// Independent runtime/recovery seams that compile without Runtime types.
/// Storage interruption contracts are covered by
/// <see cref="DysonTurnInterruptionPersistenceTests"/>.
/// </summary>
/// <remarks>
/// Future tests (after Runtime + facade extraction). Do not edit
/// <c>DysonUiHost</c> or planned <c>Runtime/</c> files from this workstream.
/// <list type="number">
/// <item>
/// <b>DysonSessionRuntimeRegistryTests</b> — two simulated circuit scopes for
/// the same subject resolve one retained runtime + live session; different
/// subjects cannot share state; disposing a circuit scope must not dispose an
/// active runtime. Composition seam:
/// <c>IDysonSessionRuntimeScopeFactory</c> +
/// <c>DysonCloudSubjectScope.TryBind</c> (Cloud) /
/// <c>DysonSubjects.Local</c> (local). Reuse
/// <c>DysonTempDb.MutableSubjectContext</c>.
/// </item>
/// <item>
/// <b>DysonSessionRuntimeDisconnectTests</b> — delayed fake
/// <c>DysonAgentProvider</c> (TCS / hang-until-cancel, not Demo's 180ms mock
/// tools). Dispose facade/circuit while PromptAsync is in flight; turn must
/// finish + persist; second facade attaches the same in-memory session, busy
/// / queue state, and final turn with no second inference. Contrast today's
/// <c>DysonUiHost.DisposeAsync</c> → <c>UnhookAllSessions</c> which cancels
/// <c>_promptCtsBySession</c>. Host construction today:
/// <c>DysonUiHostDeferredModelSwitchTests</c>.
/// </item>
/// <item>
/// <b>DysonSessionRuntimeStreamingTests</b> — background text/tool events
/// coalesce without a circuit; attach a new facade and read
/// <c>StreamingPreview</c> / finalized assistant text. Builds on
/// <c>DysonAgentTurnStreamingPreviewRaceTests</c>. Must not throw after
/// disposed renderer.
/// </item>
/// <item>
/// <b>Cancellation / shutdown</b> — circuit disconnect does not cancel;
/// explicit <c>CancelPrompt</c> / Stop / Stop-all do; registry
/// application-stopping cancels + awaits prompt tasks and persistence
/// flushes. Distinct from HTTP/circuit CT.
/// </item>
/// <item>
/// <b>Recovery service</b> — <c>DysonSessionRecoveryService</c> on first
/// subject attach: scan via
/// <c>ListActiveSessionsWithUnfinishedTurnsAsync</c>;
/// <c>FinalizeIncompleteTools</c>; stamp turn complete + persist repaired
/// <c>ToolStateJson</c> + <c>InterruptionReason</c>; append
/// <c>TurnInterrupted</c> log; root stays <c>Active</c> (not busy, no
/// replay); active child → <c>Interrupted</c> (no synthesized parent
/// report); sweep is idempotent. Storage primitives already locked in
/// <c>DysonTurnInterruptionPersistenceTests</c>.
/// </item>
/// <item>
/// <b>Subject isolation</b> — Cloud-shaped scopes: reattached circuit sees
/// only its captured subject. Extend
/// <c>DysonSubjectIsolationTests.AssertSessionIsolation</c>.
/// </item>
/// <item>
/// <b>Regression</b> — graph hydrate
/// (<c>DysonSubagentRestoreTests</c> + <c>ListChildSessionsAsync</c>),
/// auto-turn / task-review (<c>DysonTaskLifecycleTests</c> /
/// future <c>DysonUiHostTaskLifecycleTests</c>), deferred model switch
/// (<c>DysonUiHostDeferredModelSwitchTests</c>), child report FIFO, delete
/// cascade, status/icon
/// (<c>DysonSubagentHostLogic.IsRunning</c> treats Interrupted as not
/// running).
/// </item>
/// <item>
/// <b>Reconnect / reload</b> — Home reads last focused session id (work-dir
/// scoped localStorage via theme.js helper) →
/// <c>AttachOrFocusSessionAsync</c>. Live process: same runtime object.
/// Process restart: recovery then cold load. ReconnectModal success keeps
/// facade; rejected circuit reloads + attaches. No duplicate provider /
/// child / auto-turn. UI banner in <c>TurnBlock</c> /
/// <c>SessionSubagentOverview</c>.
/// </item>
/// </list>
/// </remarks>
public class DysonSessionRecoverySeamTests
{
    [Fact]
    public async Task Child_graph_status_meta_and_turn_interrupted_log_round_trip()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var _keepAlive = conn;
        var sessions = DysonTempDb.Sessions(accessor);

        var root = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 0,
            AgentMode = DysonAgentModes.Work,
            Title = "root-unfinished",
            SystemPromptSnapshot = "recovery-seam",
        });
        Assert.False(root.IsError, root.IsError ? root.Error : null);

        var child = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 1,
            ParentSessionId = root.Value,
            AgentMode = DysonAgentModes.Explore,
            Title = "child-active",
            SystemPromptSnapshot = "child",
        });
        Assert.False(child.IsError, child.IsError ? child.Error : null);

        var turnId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var live = CreateMixedToolTurn(turnId, assistantText: "partial reply");
        var entity = DysonTurnPersistence.ToEntity(live, root.Value, sequence: 1);
        Assert.Null(entity.CompletedUtc);

        var upsert = await sessions.UpsertTurnAsync(entity);
        Assert.False(upsert.IsError, upsert.IsError ? upsert.Error : null);

        var interrupted = await sessions.AppendLogAsync(
            DysonTurnPersistence.CreateTurnInterruptedLog(
                root.Value,
                live,
                DysonTurnInterruptionReasons.ApplicationRestart));
        Assert.False(interrupted.IsError, interrupted.IsError ? interrupted.Error : null);

        var childMeta = await sessions.UpdateSessionMetaAsync(new DysonSessionMetaUpdate
        {
            SessionId = child.Value,
            Status = DysonSessionStatus.Interrupted,
            Title = "child-interrupted",
        });
        Assert.False(childMeta.IsError, childMeta.IsError ? childMeta.Error : null);

        var childLog = await sessions.AppendLogAsync(DysonSessionLogPayload.CreateEntry(
            child.Value,
            DysonSessionLogKind.SessionStatusChanged,
            new DysonSessionLogSessionStatusChanged(
                DysonSessionStatus.Interrupted,
                DysonTurnInterruptionReasons.ApplicationRestart)));
        Assert.False(childLog.IsError, childLog.IsError ? childLog.Error : null);

        var rootsOnly = await sessions.ListSessionsAsync(rootsOnly: true);
        Assert.False(rootsOnly.IsError, rootsOnly.IsError ? rootsOnly.Error : null);
        Assert.Contains(rootsOnly.Value, s => s.Id == root.Value && s.Status == DysonSessionStatus.Active);
        Assert.DoesNotContain(rootsOnly.Value, s => s.Id == child.Value);

        var children = await sessions.ListChildSessionsAsync(root.Value);
        Assert.False(children.IsError, children.IsError ? children.Error : null);
        var childSummary = Assert.Single(children.Value);
        Assert.Equal(child.Value, childSummary.Id);
        Assert.Equal(DysonSessionStatus.Interrupted, childSummary.Status);
        Assert.Equal("child-interrupted", childSummary.Title);

        var fullRoot = await sessions.GetFullSessionAsync(root.Value);
        Assert.False(fullRoot.IsError, fullRoot.IsError ? fullRoot.Error : null);
        Assert.Equal(DysonSessionStatus.Active, fullRoot.Value.Session.Status);
        var persisted = Assert.Single(fullRoot.Value.Turns);
        Assert.Equal(turnId, persisted.Id);
        Assert.Null(persisted.CompletedUtc);
        Assert.Equal("partial reply", persisted.AssistantText);
        Assert.Contains("grep-done", persisted.ToolStateJson, StringComparison.Ordinal);
        Assert.Contains("shell-hang", persisted.ToolStateJson, StringComparison.Ordinal);
        var turnLog = Assert.Single(
            fullRoot.Value.Logs,
            l => l.Kind == nameof(DysonSessionLogKind.TurnInterrupted));
        var payload = DysonSessionLogPayload.Deserialize<DysonSessionLogTurnInterrupted>(
            turnLog.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(turnId, payload.TurnId);
        Assert.Equal(DysonTurnInterruptionReasons.ApplicationRestart, payload.Reason);

        var fullChild = await sessions.GetFullSessionAsync(child.Value);
        Assert.False(fullChild.IsError, fullChild.IsError ? fullChild.Error : null);
        Assert.Equal(DysonSessionStatus.Interrupted, fullChild.Value.Session.Status);
        Assert.Equal(
            nameof(DysonSessionLogKind.SessionStatusChanged),
            Assert.Single(fullChild.Value.Logs).Kind);
    }

    [Fact]
    public void FinalizeIncompleteTools_repairs_working_tools_without_inventing_assistant_text()
    {
        const string reason = "Interrupted by application restart; no model/tool call was replayed.";
        var turn = CreateMixedToolTurn(Guid.NewGuid(), assistantText: "kept fragment");

        turn.FinalizeIncompleteTools(reason);
        turn.FinalizeIncompleteTools(reason);

        var completed = Assert.Single(
            turn.TrackedToolCalls,
            t => t.Call.CallId == "grep-done");
        Assert.Equal(DysonToolCallStatus.Completed, completed.Status);
        Assert.False(completed.Result?.IsError ?? true);
        Assert.Equal("2 hits", completed.Result?.Content);

        var hung = Assert.Single(
            turn.TrackedToolCalls,
            t => t.Call.CallId == "shell-hang");
        Assert.Equal(DysonToolCallStatus.Failed, hung.Status);
        Assert.True(hung.Result?.IsError);
        Assert.Equal(reason, hung.Result?.Content);

        Assert.Equal(2, turn.ResponseLog.Count);
        Assert.Equal("kept fragment", turn.AssistantText);
        Assert.Null(turn.CompletedUtc);

        var entity = DysonTurnPersistence.ToEntity(turn, Guid.NewGuid(), sequence: 1);
        Assert.Equal("kept fragment", entity.AssistantText);
        Assert.Null(entity.CompletedUtc);

        var restored = new DysonAgentTurn { Id = turn.Id };
        DysonTurnToolStateSerializer.ApplyToTurn(restored, entity.ToolStateJson);
        Assert.Equal(DysonToolCallStatus.Completed, Assert.Single(
            restored.TrackedToolCalls,
            t => t.Call.CallId == "grep-done").Status);
        Assert.Equal(DysonToolCallStatus.Failed, Assert.Single(
            restored.TrackedToolCalls,
            t => t.Call.CallId == "shell-hang").Status);
        Assert.Null(restored.AssistantText);
    }

    [Fact]
    public void RestoreFromPersisted_finalizes_incomplete_tools_but_does_not_stamp_CompletedUtc()
    {
        var turnId = Guid.NewGuid();
        var live = CreateMixedToolTurn(turnId, assistantText: "partial");
        live.InterruptionReason = DysonTurnInterruptionReasons.ApplicationRestart;
        var entity = DysonTurnPersistence.ToEntity(live, Guid.NewGuid(), sequence: 1);
        entity.CompletedUtc = null;

        var session = new StubSession();
        session.RestoreForTest(new DysonPersistedSession
        {
            Session = new DysonSessionEntity
            {
                Id = Guid.NewGuid(),
                RuntimeId = 0,
                AgentMode = DysonAgentModes.Work,
                Status = DysonSessionStatus.Active,
                Title = "restore",
                SystemPromptSnapshot = "snap",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                LastActivityUtc = DateTime.UtcNow,
            },
            Turns = [entity],
            Logs = [],
            Todos = [],
        });

        var restored = Assert.Single(session.Turns);
        Assert.Null(restored.CompletedUtc);
        Assert.Equal("partial", restored.AssistantText);
        Assert.Equal(DysonTurnInterruptionReasons.ApplicationRestart, restored.InterruptionReason);
        Assert.Equal(DysonSessionStatus.Active, session.Status);
        Assert.False(session.IsTerminal);
        Assert.Equal(
            DysonToolCallStatus.Failed,
            Assert.Single(restored.TrackedToolCalls, t => t.Call.CallId == "shell-hang").Status);
        Assert.Equal(
            "Tool call did not complete (cancelled or interrupted).",
            Assert.Single(restored.TrackedToolCalls, t => t.Call.CallId == "shell-hang").Result?.Content);
        Assert.Equal(
            DysonToolCallStatus.Completed,
            Assert.Single(restored.TrackedToolCalls, t => t.Call.CallId == "grep-done").Status);
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

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void RestoreForTest(DysonPersistedSession state) => RestoreFromPersisted(state);

        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
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
