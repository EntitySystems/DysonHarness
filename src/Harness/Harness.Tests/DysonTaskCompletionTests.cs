using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert-only CompleteTask → TaskCompletionConfirm → Confirm/Continue → ReportSummary/Continuation
/// queue wiring (Xunit Fact). /// 
/// </summary>
public class DysonTaskCompletionTests
{
    [Fact]
    public void Run()
    {
        AssertFactoriesAndTerminalGate();
        AssertCompleteTaskEnqueuesConfirm();
        AssertConfirmAndContinuePhaseGuard();
        AssertConfirmEnqueuesReportSummary();
        AssertContinueEnqueuesContinuation();
    }

    private static void AssertFactoriesAndTerminalGate()
    {
        var confirm = DysonTaskCompletionFlow.CreateCompletionConfirmTurn("done");
        if (confirm.Kind != DysonAgentTurnKind.TaskCompletionConfirm
            || string.IsNullOrWhiteSpace(confirm.Instruction)
            || !confirm.Instruction.Contains(DysonTaskCompletionFlow.ConfirmInstruction, StringComparison.Ordinal)
            || !confirm.Instruction.Contains("done", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CreateCompletionConfirmTurn fields mismatch.");
        }

        var report = DysonTaskCompletionFlow.CreateReportSummaryTurn("ok");
        if (report.Kind != DysonAgentTurnKind.ReportSummary
            || !report.Instruction!.Contains(DysonTaskCompletionFlow.ReportSummaryInstruction, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CreateReportSummaryTurn fields mismatch.");
        }

        var cont = DysonTaskCompletionFlow.CreateContinuationTurn("not done", "finish tests");
        if (cont.Kind != DysonAgentTurnKind.Continuation
            || !cont.Instruction!.Contains("finish tests", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CreateContinuationTurn fields mismatch.");
        }

        if (!DysonTaskCompletionFlow.ShouldMarkTerminalAfterTurn(DysonAgentTurnKind.ReportSummary)
            || DysonTaskCompletionFlow.ShouldMarkTerminalAfterTurn(DysonAgentTurnKind.TaskCompletionConfirm)
            || DysonTaskCompletionFlow.ShouldMarkTerminalAfterTurn(DysonAgentTurnKind.Continuation)
            || DysonTaskCompletionFlow.ShouldMarkTerminalAfterTurn(DysonAgentTurnKind.Normal))
        {
            throw new InvalidOperationException(
                "ShouldMarkTerminalAfterTurn must be true only for ReportSummary.");
        }
    }

    private static void AssertCompleteTaskEnqueuesConfirm()
    {
        var session = new StubSession();
        session.ConfigureRootForTest();
        using var http = new HttpClient();
        var executor = new DysonWorkspaceToolExecutor(session, Path.GetTempPath(), http);

        var result = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "c1",
            ToolName = "CompleteTask",
            Stage = 0,
            ArgumentsJson = """{"summary":"shipped feature X"}""",
        }).GetAwaiter().GetResult();

        if (result.IsError)
            throw new InvalidOperationException("CompleteTask should succeed: " + result.Content);

        if (!session.TryDequeuePendingTurn(out var turn)
            || turn.Kind != DysonAgentTurnKind.TaskCompletionConfirm
            || turn.Instruction is null
            || !turn.Instruction.Contains("shipped feature X", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "CompleteTask must enqueue a TaskCompletionConfirm turn with the summary.");
        }

        if (!result.Content.Contains("TaskCompletionConfirm", StringComparison.Ordinal))
            throw new InvalidOperationException("CompleteTask success JSON should note nextTurnKind.");
    }

    private static void AssertConfirmAndContinuePhaseGuard()
    {
        var session = new StubSession();
        session.ConfigureRootForTest();
        using var http = new HttpClient();
        var executor = new DysonWorkspaceToolExecutor(session, Path.GetTempPath(), http);

        var confirm = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "c2",
            ToolName = "ConfirmTaskComplete",
            Stage = 0,
            ArgumentsJson = "{}",
        }).GetAwaiter().GetResult();
        if (!confirm.IsError
            || confirm.Content.IndexOf("TaskCompletionConfirm", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "ConfirmTaskComplete must fail outside confirm phase: " + confirm.Content);
        }

        var cont = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "c3",
            ToolName = "ContinueWork",
            Stage = 0,
            ArgumentsJson = """{"reason":"tests failing"}""",
        }).GetAwaiter().GetResult();
        if (!cont.IsError
            || cont.Content.IndexOf("TaskCompletionConfirm", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "ContinueWork must fail outside confirm phase: " + cont.Content);
        }
    }

    private static void AssertConfirmEnqueuesReportSummary()
    {
        var session = new StubSession();
        session.ConfigureRootForTest();
        session.AddTurnForTest(DysonTaskCompletionFlow.CreateCompletionConfirmTurn("prior"));
        using var http = new HttpClient();
        var executor = new DysonWorkspaceToolExecutor(session, Path.GetTempPath(), http);

        if (!session.IsInTaskCompletionConfirmPhase)
            throw new InvalidOperationException("Expected IsInTaskCompletionConfirmPhase after confirm turn.");

        var result = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "c4",
            ToolName = "ConfirmTaskComplete",
            Stage = 0,
            ArgumentsJson = """{"rationale":"verified"}""",
        }).GetAwaiter().GetResult();

        if (result.IsError)
            throw new InvalidOperationException("ConfirmTaskComplete should succeed: " + result.Content);

        if (!session.TryDequeuePendingTurn(out var turn)
            || turn.Kind != DysonAgentTurnKind.ReportSummary
            || turn.Instruction is null
            || !turn.Instruction.Contains("verified", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "ConfirmTaskComplete must enqueue a ReportSummary turn.");
        }
    }

    private static void AssertContinueEnqueuesContinuation()
    {
        var session = new StubSession();
        session.ConfigureRootForTest();
        session.AddTurnForTest(DysonTaskCompletionFlow.CreateCompletionConfirmTurn("prior"));
        using var http = new HttpClient();
        var executor = new DysonWorkspaceToolExecutor(session, Path.GetTempPath(), http);

        var result = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "c5",
            ToolName = "ContinueWork",
            Stage = 0,
            ArgumentsJson = """{"reason":"gaps","remainingWork":"add tests"}""",
        }).GetAwaiter().GetResult();

        if (result.IsError)
            throw new InvalidOperationException("ContinueWork should succeed: " + result.Content);

        if (!session.TryDequeuePendingTurn(out var turn)
            || turn.Kind != DysonAgentTurnKind.Continuation
            || turn.Instruction is null
            || !turn.Instruction.Contains("add tests", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "ContinueWork must enqueue a Continuation turn.");
        }

        // Empty ContinueWork args should fail.
        session.AddTurnForTest(DysonTaskCompletionFlow.CreateCompletionConfirmTurn("again"));
        var empty = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "c6",
            ToolName = "ContinueWork",
            Stage = 0,
            ArgumentsJson = "{}",
        }).GetAwaiter().GetResult();
        if (!empty.IsError)
            throw new InvalidOperationException("ContinueWork without reason/remainingWork must fail.");
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void ConfigureRootForTest() => ConfigureRootInterAgentTools();

        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

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
