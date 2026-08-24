using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Engine: StopSubagentAsync must not restart a child by draining a pending turn
/// onto a new CTS (always-cancellable session stop, wave 1).
/// </summary>
public class DysonStopSubagentTests
{
    [Fact]
    public async Task StopSubagentAsync_does_not_drain_pending_child_turn()
    {
        var parent = new ParentSession();
        var child = new HangingChildSession();
        parent.RegisterForTest(child);

        child.StartBackgroundPrompt();
        await child.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, child.PromptCount);
        Assert.True(child.HasActiveBackgroundRun);

        child.EnqueuePendingTurn(new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "queued after stop must not run",
            StartedUtc = DateTime.UtcNow,
        });
        Assert.True(child.HasPendingTurn);

        var stopped = await parent.StopSubagentAsync(child.Id, "stop for test");
        Assert.True(stopped.IsSuccess, stopped.IsError ? stopped.Error : null);

        await child.PromptFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        Assert.Equal(DysonSessionStatus.Stopped, child.Status);
        Assert.False(child.HasActiveBackgroundRun);
        Assert.False(child.HasPendingTurn);
        Assert.Equal(1, child.PromptCount);
        Assert.Equal("stop for test", child.LastReportSummary);
    }

    [Fact]
    public async Task KickOffChildPrompt_on_cancel_does_not_start_pending_turn()
    {
        var parent = new ParentSession();
        var child = new HangingChildSession();
        parent.RegisterForTest(child);

        child.StartBackgroundPrompt();
        await child.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        child.EnqueuePendingTurn(new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "must stay queued on cancel",
            StartedUtc = DateTime.UtcNow,
        });

        child.CancelBackgroundRunForTest();
        await child.PromptFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        Assert.Equal(DysonSessionStatus.Active, child.Status);
        Assert.False(child.HasActiveBackgroundRun);
        Assert.True(child.HasPendingTurn);
        Assert.Equal(1, child.PromptCount);
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class ParentSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
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

    private sealed class HangingChildSession() : DysonAgentSession(
        DysonAgentModes.Explore,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public int PromptCount;
        public readonly TaskCompletionSource PromptStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource PromptFinished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void StartBackgroundPrompt()
        {
            var runCts = new CancellationTokenSource();
            AttachBackgroundRun(runCts);
            KickOffChildPrompt(
                this,
                new DysonAgentTurn
                {
                    Kind = DysonAgentTurnKind.Normal,
                    Instruction = "hold until cancelled",
                    StartedUtc = DateTime.UtcNow,
                },
                runCts);
        }

        public void CancelBackgroundRunForTest() => CancelBackgroundRun();

        public override async Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PromptCount);
            PromptStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            finally
            {
                PromptFinished.TrySetResult();
            }
        }

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
