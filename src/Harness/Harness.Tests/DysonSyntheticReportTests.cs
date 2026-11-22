using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: synthetic failed reports reuse SubmitSubagentReportAsync; Stopped carve-out
/// is harness-only; idle children get ChildReportReminder before synthesis.
/// A synthetic report closes the parent's loop without relabelling the child: a session the
/// user/parent halted stays Stopped, so "I stopped this" stays distinguishable from "this died".
/// </summary>
public class DysonSyntheticReportTests
{
    [Fact]
    public async Task Stopped_child_is_synthesized_without_losing_its_stopped_status()
    {
        var parent = new StubSession();
        var child = new StubSession(DysonAgentModes.Drone);
        parent.RegisterForTest(child);

        Assert.True(child.TryMarkTerminal(DysonSessionStatus.Stopped, "stopped by parent"));

        var agentRetry = await child.SubmitSubagentReportAsync("should reject").ConfigureAwait(false);
        Assert.True(agentRetry.IsError, agentRetry.IsError ? null : "expected agent submit rejected");
        Assert.Contains("already Stopped", agentRetry.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DysonSessionStatus.Stopped, child.Status);
        Assert.False(parent.TryDequeueInterrupt(out _));

        var synthesized = await child.SubmitSyntheticReportAsync("stopped by parent").ConfigureAwait(false);
        Assert.True(synthesized.IsSuccess, synthesized.IsError ? synthesized.Error : null);
        // The report lands (summary + parent notification below) but the halt is preserved.
        Assert.Equal(DysonSessionStatus.Stopped, child.Status);
        Assert.Equal("stopped by parent", child.LastReportSummary);

        Assert.True(parent.TryDequeueInterrupt(out var interrupt));
        Assert.Equal(DysonAgentInterruptKind.SubagentFailed, interrupt.Kind);
        Assert.Equal(child.Id, interrupt.SubagentId);
        Assert.Equal("stopped by parent", interrupt.Summary);
        Assert.False(parent.TryDequeueInterrupt(out _));

        // Idempotency now rests on the accepted-report flag rather than on the status having been
        // overwritten to Failed: StopSubagentAsync and the cancelled-run error path both synthesize
        // for the same stop, and the parent must be told exactly once.
        var secondSynthetic = await child.SubmitSyntheticReportAsync("again").ConfigureAwait(false);
        Assert.True(secondSynthetic.IsError, "expected second synthetic rejected (idempotent)");
        Assert.Contains("already Stopped", secondSynthetic.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("stopped by parent", child.LastReportSummary);
        Assert.False(parent.TryDequeueInterrupt(out _));
    }

    [Fact]
    public async Task Already_reported_child_is_not_synthesized_again()
    {
        var parent = new StubSession();
        var child = new StubSession(DysonAgentModes.Drone);
        parent.RegisterForTest(child);

        var first = await child.SubmitSubagentReportAsync("done").ConfigureAwait(false);
        Assert.True(first.IsSuccess, first.IsError ? first.Error : null);
        Assert.True(parent.TryDequeueInterrupt(out var firstInterrupt));
        Assert.Equal(DysonAgentInterruptKind.SubagentCompleted, firstInterrupt.Kind);

        await child.ApplyChildReportWatchAsync().ConfigureAwait(false);
        Assert.False(child.HasPendingTurn);
        Assert.Equal(0, child.ChildReportRemindersSent);
        Assert.False(parent.TryDequeueInterrupt(out _));

        var synthetic = await child.SubmitSyntheticReportAsync("should not land").ConfigureAwait(false);
        Assert.True(synthetic.IsError);
        Assert.False(parent.TryDequeueInterrupt(out _));
    }

    [Fact]
    public async Task Idle_child_is_reminded_twice_then_synthesized()
    {
        var parent = new StubSession();
        var child = new StubSession(DysonAgentModes.Drone);
        parent.RegisterForTest(child);

        await child.ApplyChildReportWatchAsync().ConfigureAwait(false);
        Assert.Equal(1, child.ChildReportRemindersSent);
        Assert.True(child.TryDequeuePendingTurn(out var first));
        Assert.Equal(DysonAgentTurnKind.ChildReportReminder, first.Kind);
        Assert.Equal(DysonChildReportWatch.ReminderInstruction, first.Instruction);
        Assert.False(parent.TryDequeueInterrupt(out _));

        await child.ApplyChildReportWatchAsync().ConfigureAwait(false);
        Assert.Equal(2, child.ChildReportRemindersSent);
        Assert.True(child.TryDequeuePendingTurn(out var second));
        Assert.Equal(DysonAgentTurnKind.ChildReportReminder, second.Kind);
        Assert.False(parent.TryDequeueInterrupt(out _));

        await child.ApplyChildReportWatchAsync().ConfigureAwait(false);
        Assert.Equal(2, child.ChildReportRemindersSent);
        Assert.False(child.HasPendingTurn);
        Assert.Equal(DysonSessionStatus.Failed, child.Status);
        Assert.Equal(DysonChildReportWatch.IdleWithoutReportReason, child.LastReportSummary);
        Assert.True(parent.TryDequeueInterrupt(out var failed));
        Assert.Equal(DysonAgentInterruptKind.SubagentFailed, failed.Kind);
        Assert.Equal(DysonChildReportWatch.IdleWithoutReportReason, failed.Summary);
        Assert.False(parent.TryDequeueInterrupt(out _));
    }

    [Fact]
    public async Task StopSubagentAsync_raises_a_failed_interrupt_but_child_stays_stopped()
    {
        var parent = new StubSession();
        var child = new StubSession(DysonAgentModes.Drone);
        parent.RegisterForTest(child);

        var stopped = await parent.StopSubagentAsync(child.Id, "stop for test").ConfigureAwait(false);
        Assert.True(stopped.IsSuccess, stopped.IsError ? stopped.Error : null);
        // SubagentFailed is how the parent learns the child is done; it is not a claim that the
        // child failed. ListMetaAgentDrones and the UI read Status, which must still say Stopped.
        Assert.Equal(DysonSessionStatus.Stopped, child.Status);
        Assert.Equal("stop for test", child.LastReportSummary);

        Assert.True(parent.TryDequeueInterrupt(out var interrupt));
        Assert.Equal(DysonAgentInterruptKind.SubagentFailed, interrupt.Kind);
        Assert.Equal("stop for test", interrupt.Summary);
        Assert.False(parent.TryDequeueInterrupt(out _));
    }

    [Fact]
    public async Task Drone_with_active_worker_is_held_not_failed()
    {
        var root = new StubSession(DysonAgentModes.MetaAgent);
        var drone = new StubSession(DysonAgentModes.MetaAgentDrone);
        var worker = new StubSession(DysonAgentModes.Drone);
        root.RegisterForTest(drone);
        drone.RegisterForTest(worker);

        // Worker Active: hold, however often the watch runs.
        for (var i = 0; i < 3; i++)
            await drone.ApplyChildReportWatchAsync().ConfigureAwait(false);
        Assert.False(drone.HasPendingTurn);
        Assert.Equal(0, drone.ChildReportRemindersSent);
        Assert.Equal(DysonSessionStatus.Active, drone.Status);
        Assert.False(root.TryDequeueInterrupt(out _));

        // Worker reports: its report is undelivered until the report turn starts, so still hold.
        Assert.True((await worker.SubmitSubagentReportAsync("done").ConfigureAwait(false)).IsSuccess);
        Assert.True(drone.TryDequeueInterrupt(out var completed));
        Assert.Equal(DysonAgentInterruptKind.SubagentCompleted, completed.Kind);
        Assert.True(drone.HasUndeliveredChildReport);
        await drone.ApplyChildReportWatchAsync().ConfigureAwait(false);
        Assert.False(drone.HasPendingTurn);
        Assert.Equal(0, drone.ChildReportRemindersSent);

        // Delivered: the old rule applies again (remind).
        await drone.PromptSubagentReportProcessingAsync(completed).ConfigureAwait(false);
        Assert.False(drone.HasUndeliveredChildReport);
        await drone.ApplyChildReportWatchAsync().ConfigureAwait(false);
        Assert.Equal(1, drone.ChildReportRemindersSent);
        Assert.True(drone.TryDequeuePendingTurn(out var reminder));
        Assert.Equal(DysonAgentTurnKind.ChildReportReminder, reminder.Kind);
        Assert.False(root.TryDequeueInterrupt(out _));
    }

    [Fact]
    public void Wait_consumed_completion_is_not_undelivered()
    {
        var drone = new StubSession(DysonAgentModes.MetaAgentDrone);
        var worker = new StubSession(DysonAgentModes.Drone);
        drone.RegisterForTest(worker);

        drone.MarkWaitConsumedCompletion(worker.Id);
        drone.NotifyCompletedForTest(worker.Id);
        Assert.False(drone.HasUndeliveredChildReport);

        // A report that lands first is cleared when a later Wait consumes it.
        drone.ClearWaitConsumedCompletion(worker.Id);
        drone.NotifyCompletedForTest(worker.Id);
        Assert.True(drone.HasUndeliveredChildReport);
        drone.MarkWaitConsumedCompletion(worker.Id);
        Assert.False(drone.HasUndeliveredChildReport);
    }

    [Theory]
    [InlineData(DysonSessionStatus.Stopped)]
    [InlineData(DysonSessionStatus.Interrupted)]
    public async Task Halted_drone_with_active_worker_still_synthesizes(DysonSessionStatus halt)
    {
        var root = new StubSession(DysonAgentModes.MetaAgent);
        var drone = new StubSession(DysonAgentModes.MetaAgentDrone);
        var worker = new StubSession(DysonAgentModes.Drone);
        root.RegisterForTest(drone);
        drone.RegisterForTest(worker);

        Assert.True(drone.TryMarkTerminal(halt, "halted"));
        await drone.ApplyChildReportWatchAsync().ConfigureAwait(false);

        Assert.Equal(halt, drone.Status);
        Assert.True(root.TryDequeueInterrupt(out var failed));
        Assert.Equal(DysonAgentInterruptKind.SubagentFailed, failed.Kind);
    }

    [Fact]
    public async Task KickOff_no_report_with_active_worker_holds_the_drone()
    {
        var root = new StubSession(DysonAgentModes.MetaAgent);
        var drone = new StubSession(DysonAgentModes.MetaAgentDrone)
        {
            PromptResult = new VoidResult<string>(DysonChildReportWatch.PromptEndedWithoutReportReason),
        };
        var worker = new StubSession(DysonAgentModes.Drone);
        root.RegisterForTest(drone);
        drone.RegisterForTest(worker);

        Assert.True((await root.TriggerSubagentEventAsync(drone.Id, "go").ConfigureAwait(false)).IsSuccess);
        await drone.WaitForBackgroundRunIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DysonSessionStatus.Active, drone.Status);
        Assert.Equal(0, drone.ChildReportRemindersSent);
        Assert.False(root.TryDequeueInterrupt(out _));
    }

    [Fact]
    public async Task KickOff_no_report_without_workers_still_fails_the_drone()
    {
        var root = new StubSession(DysonAgentModes.MetaAgent);
        var drone = new StubSession(DysonAgentModes.MetaAgentDrone)
        {
            PromptResult = new VoidResult<string>(DysonChildReportWatch.PromptEndedWithoutReportReason),
        };
        root.RegisterForTest(drone);

        Assert.True((await root.TriggerSubagentEventAsync(drone.Id, "go").ConfigureAwait(false)).IsSuccess);
        await drone.WaitForBackgroundRunIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DysonSessionStatus.Failed, drone.Status);
        Assert.True(root.TryDequeueInterrupt(out var failed));
        Assert.Equal(DysonAgentInterruptKind.SubagentFailed, failed.Kind);
    }

    /// <summary>
    /// Phase 0 (plan 367, session ce6833ff): a Stopped child still runs a parent message. That turn
    /// paused for compaction, the summary queued the after-summary Continuation, and KickOff returned
    /// on IsTerminal without starting it, so the child sat idle until the next manual message.
    /// </summary>
    [Fact]
    public async Task After_summary_continuation_of_a_parent_message_on_a_stopped_child_starts()
    {
        var root = new StubSession(DysonAgentModes.MetaAgent);
        var drone = new StubSession(DysonAgentModes.MetaAgentDrone) { QueueContinuationOnFirstPrompt = true };
        root.RegisterForTest(drone);
        Assert.True(drone.TryMarkTerminal(DysonSessionStatus.Stopped, "Stopped by user."));

        Assert.True((await root.TriggerSubagentEventAsync(drone.Id, "go on").ConfigureAwait(false)).IsSuccess);
        var resumed = await drone.ContinuationStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await drone.WaitForBackgroundRunIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(resumed.AfterSummaryResume);
        Assert.False(drone.HasPendingTurn);
        Assert.Equal(DysonSessionStatus.Stopped, drone.Status);
    }

    [Fact]
    public async Task Stop_during_the_run_wins_over_a_late_after_summary_continuation()
    {
        var root = new StubSession(DysonAgentModes.MetaAgent);
        var drone = new StubSession(DysonAgentModes.MetaAgentDrone) { QueueContinuationOnFirstPrompt = true };
        root.RegisterForTest(drone);
        drone.BeforeContinuationQueued = () => root.StopSubagentAsync(drone.Id, "stop wins");

        Assert.True((await root.TriggerSubagentEventAsync(drone.Id, "go on").ConfigureAwait(false)).IsSuccess);
        await drone.ContinuationQueued.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await drone.WaitForBackgroundRunIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(drone.ContinuationStarted.Task.IsCompleted);
        Assert.Equal(DysonSessionStatus.Stopped, drone.Status);
    }

    [Fact]
    public async Task Stop_marked_mid_run_before_its_cancel_does_not_start_the_continuation()
    {
        var root = new StubSession(DysonAgentModes.MetaAgent);
        var drone = new StubSession(DysonAgentModes.MetaAgentDrone) { QueueContinuationOnFirstPrompt = true };
        root.RegisterForTest(drone);
        // StopSubagentAsync marks Stopped before it cancels the run: the run must not read that as "began Stopped".
        drone.BeforeContinuationQueued = () =>
        {
            Assert.True(drone.TryMarkTerminal(DysonSessionStatus.Stopped, "Stopped by user."));
            return Task.CompletedTask;
        };

        Assert.True((await root.TriggerSubagentEventAsync(drone.Id, "go on").ConfigureAwait(false)).IsSuccess);
        await drone.ContinuationQueued.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await drone.WaitForBackgroundRunIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(drone.ContinuationStarted.Task.IsCompleted);
        Assert.Equal(DysonSessionStatus.Stopped, drone.Status);
    }

    private static async Task<StubSession> IdleChildWithOpenTodo(StubSession parent, string mode = DysonAgentModes.MetaAgentDrone)
    {
        var child = new StubSession(mode);
        parent.RegisterForTest(child);
        var todo = await child.CreateTodoAsync("verify-it", "Finish verification", DysonSessionTodoStatus.Ongoing)
            .ConfigureAwait(false);
        Assert.True(todo.IsSuccess, todo.IsError ? todo.Error : null);
        // Unstamped, as on the KickOff path: CompletedUtc is only set when a host persists the turn.
        child.AddTurnForTest(DysonAgentSession.CreateNormalTurn("work"));
        return child;
    }

    private static DysonAgentTurn Completed(DysonAgentTurn turn)
    {
        turn.CompletedUtc = DateTime.UtcNow;
        return turn;
    }

    [Fact]
    public async Task Idle_child_with_open_todos_gets_one_reflection_before_reminders()
    {
        var root = new StubSession(DysonAgentModes.MetaAgent);
        var child = await IdleChildWithOpenTodo(root);

        await child.ApplyChildReportWatchAsync().ConfigureAwait(false);
        Assert.Equal(0, child.ChildReportRemindersSent);
        Assert.True(child.TryDequeuePendingTurn(out var reflection));
        Assert.Equal(DysonAgentTurnKind.TaskEndReflect, reflection.Kind);
        Assert.StartsWith(DysonTaskEndReflectFlow.ChildInstruction, reflection.Instruction, StringComparison.Ordinal);
        Assert.Contains("`verify-it`", reflection.Instruction, StringComparison.Ordinal);
        Assert.Contains("SubmitSubagentReport", reflection.Instruction, StringComparison.Ordinal);

        // The reflection ends idle too: two reminders, then the existing failed report (D2).
        child.AddTurnForTest(Completed(reflection));
        for (var i = 1; i <= DysonChildReportWatch.MaxReminders; i++)
        {
            await child.ApplyChildReportWatchAsync().ConfigureAwait(false);
            Assert.Equal(i, child.ChildReportRemindersSent);
            Assert.True(child.TryDequeuePendingTurn(out var reminder));
            Assert.Equal(DysonAgentTurnKind.ChildReportReminder, reminder.Kind);
            child.AddTurnForTest(Completed(reminder));
        }

        Assert.False(root.TryDequeueInterrupt(out _));
        await child.ApplyChildReportWatchAsync().ConfigureAwait(false);
        Assert.Equal(DysonSessionStatus.Failed, child.Status);
        Assert.Equal(DysonChildReportWatch.IdleWithoutReportReason, child.LastReportSummary);
        Assert.True(root.TryDequeueInterrupt(out var failed));
        Assert.Equal(DysonAgentInterruptKind.SubagentFailed, failed.Kind);
    }

    [Fact]
    public async Task Idle_after_a_compaction_continuation_gets_the_reflection()
    {
        var root = new StubSession(DysonAgentModes.MetaAgent);
        var child = await IdleChildWithOpenTodo(root);
        child.AddTurnForTest(Completed(DysonCompactionFlow.CreateFreshContinuationTurn(child)));

        await child.ApplyChildReportWatchAsync().ConfigureAwait(false);
        Assert.True(child.TryDequeuePendingTurn(out var reflection));
        Assert.Equal(DysonAgentTurnKind.TaskEndReflect, reflection.Kind);
        Assert.Equal(0, child.ChildReportRemindersSent);
    }

    [Fact]
    public async Task Open_todos_with_an_active_worker_or_undelivered_report_hold()
    {
        var root = new StubSession(DysonAgentModes.MetaAgent);
        var drone = await IdleChildWithOpenTodo(root);
        var worker = new StubSession(DysonAgentModes.Drone);
        drone.RegisterForTest(worker);

        await drone.ApplyChildReportWatchAsync().ConfigureAwait(false);
        Assert.False(drone.HasPendingTurn);

        Assert.True((await worker.SubmitSubagentReportAsync("done").ConfigureAwait(false)).IsSuccess);
        Assert.True(drone.HasUndeliveredChildReport);
        Assert.False(DysonTaskEndReflectFlow.TryCreateForChild(drone, out _));
        await drone.ApplyChildReportWatchAsync().ConfigureAwait(false);
        Assert.False(drone.HasPendingTurn);
        Assert.Equal(0, drone.ChildReportRemindersSent);
    }

    [Fact]
    public async Task No_reflection_while_a_summary_runs_a_message_is_held_or_a_parent_event_waits()
    {
        var root = new StubSession(DysonAgentModes.MetaAgent);
        var child = await IdleChildWithOpenTodo(root);

        var ask = child.TriggerParentEventAsync("message", "question?");
        Assert.True(child.HasPendingParentEventWait);
        Assert.False(DysonTaskEndReflectFlow.TryCreateForChild(child, out _));

        var release = new TaskCompletionSource<VoidResult<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var summary = DysonCompactionFlow.RunAsync(
            child, endedTurn: null, resume: false, (_, _) => release.Task, CancellationToken.None);
        Assert.True(child.IsSummarizingForInterrupt);

        // Busy (parent-event wait) and summarizing: the parent message is held, not delivered.
        Assert.True((await root.TriggerSubagentEventAsync(child.Id, "held").ConfigureAwait(false)).IsSuccess);
        Assert.Equal(1, child.HeldInterruptCount);
        Assert.False(DysonTaskEndReflectFlow.TryCreateForChild(child, out _));

        release.SetResult(VoidResult<string>.AsError("test summary failed"));
        await summary.ConfigureAwait(false);
        Assert.False(child.IsSummarizingForInterrupt);
        Assert.Equal(1, child.HeldInterruptCount);
        Assert.False(DysonTaskEndReflectFlow.TryCreateForChild(child, out _));

        child.ClearPendingTurns();
        child.CancelPendingParentEventWait("test done");
        await ask.ConfigureAwait(false);
        Assert.Equal(0, child.HeldInterruptCount);
        Assert.False(child.HasPendingParentEventWait);
        Assert.True(DysonTaskEndReflectFlow.TryCreateForChild(child, out _));
    }

    [Fact]
    public async Task Complete_todos_get_the_plain_reminder()
    {
        var root = new StubSession(DysonAgentModes.MetaAgent);
        var child = new StubSession(DysonAgentModes.MetaAgentDrone);
        root.RegisterForTest(child);
        Assert.True((await child.CreateTodoAsync("done", "Done", DysonSessionTodoStatus.Complete).ConfigureAwait(false)).IsSuccess);
        child.AddTurnForTest(Completed(DysonAgentSession.CreateNormalTurn("work")));

        await child.ApplyChildReportWatchAsync().ConfigureAwait(false);
        Assert.Equal(1, child.ChildReportRemindersSent);
        Assert.True(child.TryDequeuePendingTurn(out var reminder));
        Assert.Equal(DysonAgentTurnKind.ChildReportReminder, reminder.Kind);
    }

    [Fact]
    public async Task Root_and_voice_sidecar_get_no_reflection()
    {
        var root = new StubSession(DysonAgentModes.MetaAgent);
        Assert.True((await root.CreateTodoAsync("open", "Open").ConfigureAwait(false)).IsSuccess);
        root.AddTurnForTest(Completed(DysonAgentSession.CreateNormalTurn("work")));
        await root.ApplyChildReportWatchAsync().ConfigureAwait(false);
        Assert.False(root.HasPendingTurn);

        var sidecar = await IdleChildWithOpenTodo(new StubSession(DysonAgentModes.MetaAgent), DysonAgentModes.VoiceSidecar);
        Assert.False(DysonTaskEndReflectFlow.TryCreateForChild(sidecar, out _));
    }

    [Fact]
    public async Task KickOff_no_report_with_open_todos_reflects_then_reminds_then_fails()
    {
        var root = new StubSession(DysonAgentModes.MetaAgent);
        var drone = new StubSession(DysonAgentModes.MetaAgentDrone)
        {
            PromptResult = new VoidResult<string>(DysonChildReportWatch.PromptEndedWithoutReportReason),
        };
        root.RegisterForTest(drone);
        Assert.True((await drone.CreateTodoAsync("open", "Open", DysonSessionTodoStatus.Ongoing).ConfigureAwait(false)).IsSuccess);

        Assert.True((await root.TriggerSubagentEventAsync(drone.Id, "go").ConfigureAwait(false)).IsSuccess);
        await drone.TerminalForTest.WaitAsync(TimeSpan.FromSeconds(30));

        DysonAgentTurnKind[] kinds;
        lock (drone.Prompted)
            kinds = drone.Prompted.Skip(1).Select(t => t.Kind).ToArray();
        Assert.Equal(
            [DysonAgentTurnKind.TaskEndReflect, DysonAgentTurnKind.ChildReportReminder, DysonAgentTurnKind.ChildReportReminder],
            kinds);
        Assert.Equal(DysonSessionStatus.Failed, drone.Status);
        Assert.Equal(DysonChildReportWatch.IdleWithoutReportReason, drone.LastReportSummary);
    }

    [Fact]
    public async Task Root_session_watch_is_a_no_op()
    {
        var root = new StubSession();
        await root.ApplyChildReportWatchAsync().ConfigureAwait(false);
        Assert.False(root.HasPendingTurn);
        Assert.Equal(DysonSessionStatus.Active, root.Status);
        Assert.Equal(0, root.ChildReportRemindersSent);
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession : DysonAgentSession
    {
        public StubSession(string mode = DysonAgentModes.Work)
            : base(mode, new DysonAgentSessionConfig(), new StubProvider())
        {
        }

        public void RegisterForTest(DysonAgentSession child) => RegisterSubagent(child);

        public void NotifyCompletedForTest(int subagentId) => NotifySubagentCompleted(subagentId, "done");

        public VoidResult<string> PromptResult { get; init; } = VoidResult<string>.Success;

        /// <summary>The first prompt acts like a turn paused for compaction: it queues the fresh Continuation.</summary>
        public bool QueueContinuationOnFirstPrompt { get; init; }

        public Func<Task>? BeforeContinuationQueued { get; set; }

        public readonly TaskCompletionSource ContinuationQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public readonly TaskCompletionSource<DysonAgentTurn> ContinuationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<DysonAgentTurn> Prompted { get; } = [];

        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

        public Task TerminalForTest => WaitForTerminalAsync(CancellationToken.None);

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
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override async Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
        {
            int count;
            lock (Prompted)
            {
                Prompted.Add(turn);
                count = Prompted.Count;
            }

            AddTurn(turn);
            if (turn.AfterSummaryResume)
                ContinuationStarted.TrySetResult(turn);

            if (QueueContinuationOnFirstPrompt && count == 1)
            {
                if (BeforeContinuationQueued is not null)
                    await BeforeContinuationQueued().ConfigureAwait(false);
                EnqueuePendingTurn(DysonCompactionFlow.CreateFreshContinuationTurn(this));
                ContinuationQueued.TrySetResult();
                return VoidResult<string>.Success;
            }

            return PromptResult;
        }

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
        {
            MarkChildReportDelivered(interrupt.SubagentId);
            return Task.FromResult(VoidResult<string>.Success);
        }

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
