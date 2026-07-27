using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert-only rethink soft-pause / ResumeCurrentTask / WaitForSeconds /
/// MaxToolRounds / Explore no-rethink + recap instruction wiring (Xunit Fact).
/// /// </summary>
public class DysonRethinkToolUsageTests
{
    [Fact]
    public void Run()
    {
        AssertEnumAndFactories();
        AssertRethinkInstructionContent();
        AssertMaxToolRounds();
        AssertSoftPauseEnqueuesRethink();
        AssertSoftPauseOnRethinkDoesNotReenqueue();
        AssertSoftPauseOnExploreDoesNotEnqueue();
        AssertResumePhaseGuardAndEnqueue();
        AssertWaitForSecondsRange();
    }

    private static void AssertEnumAndFactories()
    {
        if ((int)DysonAgentTurnKind.RethinkToolUsage != 10)
            throw new InvalidOperationException("DysonAgentTurnKind.RethinkToolUsage must be 10.");

        var rethink = DysonRethinkToolUsageFlow.CreateTurn();
        if (rethink.Kind != DysonAgentTurnKind.RethinkToolUsage
            || string.IsNullOrWhiteSpace(rethink.Instruction)
            || !rethink.Instruction.Contains(
                DysonRethinkToolUsageFlow.RethinkInstruction, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CreateTurn fields mismatch.");
        }

        var resume = DysonRethinkToolUsageFlow.CreateResumeTurn("ok", "finish tests");
        if (resume.Kind != DysonAgentTurnKind.Normal
            || resume.Instruction is null
            || !resume.Instruction.Contains("finish tests", StringComparison.Ordinal)
            || !resume.Instruction.Contains("ok", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CreateResumeTurn must be Normal with guidance.");
        }

        if (string.IsNullOrWhiteSpace(DysonRethinkToolUsageFlow.ExploreBudgetRecapInstruction)
            || DysonRethinkToolUsageFlow.ExploreBudgetRecapInstruction.IndexOf(
                "incomplete", StringComparison.OrdinalIgnoreCase) < 0
            || DysonRethinkToolUsageFlow.ExploreBudgetRecapInstruction.IndexOf(
                "budget", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "ExploreBudgetRecapInstruction must mention budget and incomplete findings.");
        }

        if (string.IsNullOrWhiteSpace(DysonRethinkToolUsageFlow.ExploreBudgetExhaustedFallback)
            || !DysonRethinkToolUsageFlow.ExploreBudgetExhaustedFallback.Contains(
                "incomplete", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "ExploreBudgetExhaustedFallback must note incomplete findings.");
        }
    }

    private static void AssertRethinkInstructionContent()
    {
        var text = DysonRethinkToolUsageFlow.RethinkInstruction;
        if (text.IndexOf("readonly", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("RethinkInstruction must require readonly tools.");
        if (text.IndexOf("ResumeCurrentTask", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("RethinkInstruction must mention ResumeCurrentTask.");
        if (text.IndexOf("StartSubagent", StringComparison.Ordinal) < 0
            || text.IndexOf("Explore", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                "RethinkInstruction must allow StartSubagent Explore.");
        }
        if (text.IndexOf("WaitForSubagent", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("RethinkInstruction must require WaitForSubagent.");
        if (text.IndexOf("WaitForSeconds", StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("RethinkInstruction must not mention WaitForSeconds.");

        var prompt = DysonAgentSystemPrompts.SharedPreamble;
        if (prompt.IndexOf("120", StringComparison.Ordinal) < 0
            || prompt.IndexOf("Explore sessions do not get rethink", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "System prompt rethink blurb must note Explore budget 120 and no-rethink.");
        }
    }

    private static void AssertMaxToolRounds()
    {
        if (OpenAiCompatibleAgentSession.MaxToolRounds != 35
            || OpenAiCompatibleAgentSession.MaxToolRoundsExplore != 120)
        {
            throw new InvalidOperationException("MaxToolRounds must be 35; Explore 120.");
        }

        if (OpenAiCompatibleAgentSession.ResolveMaxToolRounds(DysonAgentModes.Work) != 35
            || OpenAiCompatibleAgentSession.ResolveMaxToolRounds(DysonAgentModes.Ask) != 35
            || OpenAiCompatibleAgentSession.ResolveMaxToolRounds(DysonAgentModes.Explore) != 120)
        {
            throw new InvalidOperationException(
                "ResolveMaxToolRounds: Explore=120, non-Explore=35.");
        }
    }

    private static void AssertSoftPauseEnqueuesRethink()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "work",
            StartedUtc = DateTime.UtcNow,
        };
        session.AddTurnForTest(turn);

        var result = OpenAiCompatibleAgentSession.SoftPauseAfterToolLoopExhaustion(session, turn, 35);
        if (result.IsError)
            throw new InvalidOperationException("Soft-pause must not return an error string.");

        if (string.IsNullOrWhiteSpace(turn.AssistantText) && string.IsNullOrWhiteSpace(turn.AgentTitle))
            throw new InvalidOperationException("Soft-pause must apply a harness assistant note.");

        if (!session.TryDequeuePendingTurn(out var pending)
            || pending.Kind != DysonAgentTurnKind.RethinkToolUsage)
        {
            throw new InvalidOperationException(
                "Soft-pause on a Normal turn must enqueue RethinkToolUsage.");
        }
    }

    private static void AssertSoftPauseOnRethinkDoesNotReenqueue()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var turn = DysonRethinkToolUsageFlow.CreateTurn();
        session.AddTurnForTest(turn);

        var result = OpenAiCompatibleAgentSession.SoftPauseAfterToolLoopExhaustion(session, turn, 35);
        if (result.IsError)
            throw new InvalidOperationException("Rethink soft-pause must not return an error string.");

        if (session.TryDequeuePendingTurn(out _))
            throw new InvalidOperationException("Soft-pause on rethink must not enqueue another rethink.");
    }

    private static void AssertSoftPauseOnExploreDoesNotEnqueue()
    {
        var session = new StubSession(DysonAgentModes.Explore);
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "explore",
            StartedUtc = DateTime.UtcNow,
        };
        session.AddTurnForTest(turn);

        var result = OpenAiCompatibleAgentSession.SoftPauseAfterToolLoopExhaustion(session, turn, 120);
        if (result.IsError)
            throw new InvalidOperationException("Explore soft-pause must not return an error string.");

        if (session.TryDequeuePendingTurn(out _))
        {
            throw new InvalidOperationException(
                "Soft-pause on Explore must not enqueue RethinkToolUsage.");
        }

        if (string.IsNullOrWhiteSpace(turn.AssistantText)
            || turn.AssistantText.IndexOf("incomplete", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "Explore soft-pause fallback must note incomplete findings.");
        }
    }

    private static void AssertResumePhaseGuardAndEnqueue()
    {
        var session = new StubSession(DysonAgentModes.Work);
        session.ConfigureRootForTest();
        using var http = new HttpClient();
        var executor = DysonWorkspaceTestFs.CreateExecutor(session, Path.GetTempPath(), http);

        var outside = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "r1",
            ToolName = "ResumeCurrentTask",
            Stage = 0,
            ArgumentsJson = """{"rationale":"progress"}""",
        }).GetAwaiter().GetResult();
        if (!outside.IsError
            || outside.Content.IndexOf("RethinkToolUsage", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "ResumeCurrentTask must fail outside rethink phase: " + outside.Content);
        }

        session.AddTurnForTest(DysonRethinkToolUsageFlow.CreateTurn());
        if (!session.IsInRethinkToolUsagePhase)
            throw new InvalidOperationException("Expected IsInRethinkToolUsagePhase after rethink turn.");

        var empty = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "r2",
            ToolName = "ResumeCurrentTask",
            Stage = 0,
            ArgumentsJson = "{}",
        }).GetAwaiter().GetResult();
        if (!empty.IsError)
            throw new InvalidOperationException(
                "ResumeCurrentTask without rationale/continuationInstructions must fail.");

        var result = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "r3",
            ToolName = "ResumeCurrentTask",
            Stage = 0,
            ArgumentsJson = """{"rationale":"justified","continuationInstructions":"finish X"}""",
        }).GetAwaiter().GetResult();

        if (result.IsError)
            throw new InvalidOperationException("ResumeCurrentTask should succeed: " + result.Content);

        if (!session.TryDequeuePendingTurn(out var turn)
            || turn.Kind != DysonAgentTurnKind.Normal
            || turn.Instruction is null
            || !turn.Instruction.Contains("finish X", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ResumeCurrentTask must enqueue a Normal turn.");
        }

        if (!result.Content.Contains("Normal", StringComparison.Ordinal))
            throw new InvalidOperationException("ResumeCurrentTask success JSON should note nextTurnKind.");
    }

    private static void AssertWaitForSecondsRange()
    {
        var session = new StubSession(DysonAgentModes.Work);
        session.ConfigureRootForTest();
        using var http = new HttpClient();
        var executor = DysonWorkspaceTestFs.CreateExecutor(session, Path.GetTempPath(), http);

        var zero = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "w0",
            ToolName = "WaitForSeconds",
            Stage = 0,
            ArgumentsJson = """{"seconds":0}""",
        }).GetAwaiter().GetResult();
        if (!zero.IsError
            || zero.Content.IndexOf("1", StringComparison.Ordinal) < 0
            || zero.Content.IndexOf("300", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("WaitForSeconds must reject 0: " + zero.Content);
        }

        var over = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "w1",
            ToolName = "WaitForSeconds",
            Stage = 0,
            ArgumentsJson = """{"seconds":301}""",
        }).GetAwaiter().GetResult();
        if (!over.IsError)
            throw new InvalidOperationException("WaitForSeconds must reject 301: " + over.Content);

        var ok = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "w2",
            ToolName = "WaitForSeconds",
            Stage = 0,
            ArgumentsJson = """{"seconds":1}""",
        }).GetAwaiter().GetResult();
        if (ok.IsError
            || !ok.Content.Contains("\"status\":\"ok\"", StringComparison.Ordinal)
            || !ok.Content.Contains("\"waitedSeconds\":1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("WaitForSeconds(1) should succeed: " + ok.Content);
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

