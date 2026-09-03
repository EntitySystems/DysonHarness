using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert-only StartNewTurn MCP enqueue / EndsCurrentTurn / missing arg (Xunit Fact).
/// </summary>
public class DysonStartNewTurnTests
{
    [Fact]
    public async Task Run()
    {
        await AssertEnqueuesNormalAndEndsTurn();
        await AssertMissingPromptInstructionsFails();
        AssertSharedPreambleMentionsStartNewTurn();
    }

    private static async Task AssertEnqueuesNormalAndEndsTurn()
    {
        var session = new StubSession(DysonAgentModes.Work);
        session.ConfigureRootForTest();
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, Path.GetTempPath(), http);

        session.AddTurnForTest(new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "do work",
            StartedUtc = DateTime.UtcNow,
        });

        const string instructions = "write the second 50-word paragraph";
        var result = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "s1",
            ToolName = "StartNewTurn",
            Stage = 0,
            ArgumentsJson = $$"""{"promptInstructions":"{{instructions}}"}""",
        }).GetAwaiter().GetResult();

        if (result.IsError)
            throw new InvalidOperationException("StartNewTurn should succeed: " + result.Content);

        if (!result.EndsCurrentTurn)
            throw new InvalidOperationException("StartNewTurn must set EndsCurrentTurn.");

        if (!session.TryDequeuePendingTurn(out var pending)
            || pending.Kind != DysonAgentTurnKind.Normal
            || pending.Instruction is null
            || !pending.Instruction.Equals(instructions, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "StartNewTurn must enqueue Normal with promptInstructions as Instruction.");
        }

        if (!result.Content.Contains("Normal", StringComparison.Ordinal)
            || !result.Content.Contains(instructions, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Success JSON should note nextTurnKind and instructions.");
        }
    }

    private static async Task AssertMissingPromptInstructionsFails()
    {
        var session = new StubSession(DysonAgentModes.Work);
        session.ConfigureRootForTest();
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, Path.GetTempPath(), http);

        var missing = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "s2",
            ToolName = "StartNewTurn",
            Stage = 0,
            ArgumentsJson = "{}",
        }).GetAwaiter().GetResult();

        if (!missing.IsError
            || missing.Content.IndexOf("promptInstructions", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "StartNewTurn must require promptInstructions: " + missing.Content);
        }

        var blank = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "s3",
            ToolName = "StartNewTurn",
            Stage = 0,
            ArgumentsJson = """{"promptInstructions":"   "}""",
        }).GetAwaiter().GetResult();

        if (!blank.IsError)
            throw new InvalidOperationException("Whitespace promptInstructions must fail: " + blank.Content);

        if (session.TryDequeuePendingTurn(out _))
            throw new InvalidOperationException("Failed StartNewTurn must not enqueue a turn.");
    }

    private static void AssertSharedPreambleMentionsStartNewTurn()
    {
        var preamble = DysonAgentSystemPrompts.SharedPreamble;
        if (preamble.IndexOf("StartNewTurn", StringComparison.Ordinal) < 0
            || preamble.IndexOf("not a substitute for ExpandThoughtProcess", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "SharedPreamble must note StartNewTurn and that it is not a substitute for ExpandThoughtProcess.");
        }
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode) : DysonAgentSession(
        mode,
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
