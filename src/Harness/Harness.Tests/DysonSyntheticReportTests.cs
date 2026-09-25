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

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

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
