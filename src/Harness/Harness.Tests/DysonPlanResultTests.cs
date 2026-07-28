using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert-only PlanResult / BeginBuildPlan kinds + PlanRelativePath restore + SubmitPlan path shape
/// + ApplyAgentMode generation bump + Plan-ready sticky visibility + Plan-mode report deferral.
/// /// </summary>
public class DysonPlanResultTests
{
    [Fact]
    public void Run()
    {
        AssertKindValue();
        AssertCreateTurnFields();
        AssertBeginBuildPlanFields();
        AssertBeginBuildPlanWithReports();
        AssertBeginBuildPlanContinuation();
        AssertCompletionAutoTurnPlanDeferral();
        AssertPersistenceRoundTrip();
        AssertSubmitPlanCatalog();
        AssertApplyAgentModeRebuild();
        AssertPlanReadyPending();
    }

    private static void AssertKindValue()
    {
        if ((int)DysonAgentTurnKind.PlanResult != 6)
            throw new InvalidOperationException("DysonAgentTurnKind.PlanResult must be 6.");
        if ((int)DysonAgentTurnKind.BeginBuildPlan != 7)
            throw new InvalidOperationException("DysonAgentTurnKind.BeginBuildPlan must be 7.");
        if ((int)DysonAgentTurnKind.SubagentReportProcessing != 8)
            throw new InvalidOperationException("DysonAgentTurnKind.SubagentReportProcessing must be 8.");
    }

    private static void AssertCreateTurnFields()
    {
        var turn = DysonPlanResultFlow.CreateTurn(".dyson/plans/demo-abcdef0123.md", "Demo Plan");
        if (turn.Kind != DysonAgentTurnKind.PlanResult
            || turn.PlanRelativePath != ".dyson/plans/demo-abcdef0123.md"
            || turn.AgentTitle != "Demo Plan"
            || turn.CompletedUtc is null
            || string.IsNullOrWhiteSpace(turn.Instruction)
            || !turn.Instruction.Contains(".dyson/plans/demo-abcdef0123.md", StringComparison.Ordinal)
            || !turn.Instruction.Contains("Do not call SubmitPlan again", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("PlanResult CreateTurn fields mismatch.");
        }

        var buildPrompt = DysonPlanResultFlow.BuildPlanUserPrompt(".dyson/plans/demo-abcdef0123.md");
        if (!buildPrompt.StartsWith(DysonPlanResultFlow.BuildPlanMarker, StringComparison.Ordinal)
            || !buildPrompt.Contains(".dyson/plans/demo-abcdef0123.md", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("BuildPlanUserPrompt must start with marker and include path.");
        }
    }

    private static void AssertBeginBuildPlanFields()
    {
        var turn = DysonBeginBuildPlanFlow.CreateTurn(".dyson/plans/build-abcdef0123.md");
        if (turn.Kind != DysonAgentTurnKind.BeginBuildPlan
            || turn.PlanRelativePath != ".dyson/plans/build-abcdef0123.md"
            || string.IsNullOrWhiteSpace(turn.Instruction)
            || !turn.Instruction.StartsWith("# Begin build plan", StringComparison.Ordinal)
            || !turn.Instruction.Contains(".dyson/plans/build-abcdef0123.md", StringComparison.Ordinal)
            || !turn.Instruction.Contains("**`## Recap`**", StringComparison.Ordinal)
            || !turn.Instruction.Contains("**`## Agent actions`**", StringComparison.Ordinal)
            || !turn.Instruction.Contains("multiple Drones", StringComparison.Ordinal)
            || !turn.Instruction.Contains("Multitasking is superior", StringComparison.Ordinal)
            || !turn.Instruction.Contains("layout-only", StringComparison.OrdinalIgnoreCase)
            || !turn.Instruction.Contains("CreateTodo", StringComparison.Ordinal)
            || !turn.Instruction.Contains(
                "More todos may be added later during implementation",
                StringComparison.Ordinal)
            || !turn.Instruction.Contains(
                "next harness turn will automatically continue and run the implementation",
                StringComparison.OrdinalIgnoreCase)
            || turn.Instruction.Contains("Do not call tools this turn", StringComparison.Ordinal)
            || turn.Instruction.Contains("same turn after the sections is OK", StringComparison.Ordinal)
            || turn.Instruction.Contains("\n## Recap\n", StringComparison.Ordinal)
            || turn.Instruction.Contains("\n## Agent actions\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("BeginBuildPlan CreateTurn fields mismatch.");
        }
    }

    private static void AssertBeginBuildPlanContinuation()
    {
        if (DysonBeginBuildPlanFlow.ContinuationPrompt
            != "Continue the plan implementation as per previous instructions. "
                + "Prefer parallel Drone multitasking (`StartSubagent`) for independent Agent actions workstreams; "
                + "Wait only for hard prerequisites. "
                + "Session todos already exist for the Agent actions checklist; add or update todos as work unfolds.")
        {
            throw new InvalidOperationException("BeginBuildPlan ContinuationPrompt text mismatch.");
        }

        if (!DysonBeginBuildPlanFlow.ShouldEnqueueBuildContinuation(DysonAgentTurnKind.BeginBuildPlan)
            || DysonBeginBuildPlanFlow.ShouldEnqueueBuildContinuation(DysonAgentTurnKind.PlanResult)
            || DysonBeginBuildPlanFlow.ShouldEnqueueBuildContinuation(DysonAgentTurnKind.Normal)
            || DysonBeginBuildPlanFlow.ShouldEnqueueBuildContinuation(DysonAgentTurnKind.Continuation))
        {
            throw new InvalidOperationException(
                "ShouldEnqueueBuildContinuation must be true only for BeginBuildPlan.");
        }
    }

    private static void AssertBeginBuildPlanWithReports()
    {
        var interrupt = new DysonAgentInterrupt
        {
            Kind = DysonAgentInterruptKind.SubagentCompleted,
            SubagentId = 7,
            PersistenceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Summary = "Mapped AuthService and token refresh.",
        };
        var block = DysonSubagentReportPrompt.FormatReportBlock(interrupt, "Explore auth");
        if (!block.Contains("**Report**", StringComparison.Ordinal)
            || block.Contains("## Report", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("FormatReportBlock must use bold Report, not ## heading.");
        }

        var turn = DysonBeginBuildPlanFlow.CreateTurn(
            ".dyson/plans/build-abcdef0123.md",
            reportBlocks: [block]);

        if (turn.Instruction is null
            || !turn.Instruction.Contains("**Explore reports to incorporate**", StringComparison.Ordinal)
            || !turn.Instruction.Contains(
                "do not start implementation this turn",
                StringComparison.OrdinalIgnoreCase)
            || turn.Instruction.Contains(
                "do not wait for another harness continuation turn",
                StringComparison.Ordinal)
            || !turn.Instruction.Contains("subagentId: 7", StringComparison.Ordinal)
            || !turn.Instruction.Contains("Mapped AuthService and token refresh.", StringComparison.Ordinal)
            || !turn.Instruction.Contains("Explore auth", StringComparison.Ordinal)
            || !turn.Instruction.Contains("**`## Recap`**", StringComparison.Ordinal)
            || turn.Instruction.Contains("## Explore reports", StringComparison.Ordinal)
            || turn.Instruction.Contains("## Report", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "BeginBuildPlan with report blocks must include Recap mandate + Explore report text.");
        }

        var continuation = DysonSubagentReportPrompt.BuildContinuationPrompt(interrupt, "Explore auth");
        if (!continuation.Contains("# Subagent report", StringComparison.Ordinal)
            || !continuation.Contains("concrete technical continuation", StringComparison.OrdinalIgnoreCase)
            || !continuation.Contains("Do not wait for another harness turn", StringComparison.Ordinal)
            || !continuation.Contains("subagentId: 7", StringComparison.Ordinal)
            || !continuation.Contains(block.Trim(), StringComparison.Ordinal)
            || continuation.Contains("## Report", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Continuation prompt must wrap the shared report block with SubagentReport mandate.");
        }

        var reportTurn = DysonSubagentReportPrompt.CreateTurn(interrupt, "Explore auth");
        if (reportTurn.Kind != DysonAgentTurnKind.SubagentReportProcessing
            || reportTurn.Instruction != continuation
            || reportTurn.CompletedUtc is not null)
        {
            throw new InvalidOperationException(
                "CreateTurn must be SubagentReportProcessing with BuildContinuationPrompt Instruction.");
        }
    }

    private static void AssertCompletionAutoTurnPlanDeferral()
    {
        if (DysonSubagentReportPrompt.ShouldDrainCompletionAutoTurn(DysonAgentModes.Plan))
            throw new InvalidOperationException("Plan mode must not drain completion auto-turns.");

        if (!DysonSubagentReportPrompt.ShouldDrainCompletionAutoTurn(DysonAgentModes.Work)
            || !DysonSubagentReportPrompt.ShouldDrainCompletionAutoTurn(DysonAgentModes.Ask))
        {
            throw new InvalidOperationException("Non-Plan modes must drain completion auto-turns.");
        }

        if (!DysonSubagentReportPrompt.IsCompletionInterrupt(DysonAgentInterruptKind.SubagentCompleted)
            || !DysonSubagentReportPrompt.IsCompletionInterrupt(DysonAgentInterruptKind.SubagentFailed)
            || !DysonSubagentReportPrompt.IsCompletionInterrupt(DysonAgentInterruptKind.SubagentStopped)
            || DysonSubagentReportPrompt.IsCompletionInterrupt(DysonAgentInterruptKind.SubagentEvent))
        {
            throw new InvalidOperationException("IsCompletionInterrupt kind mapping wrong.");
        }
    }

    private static void AssertPersistenceRoundTrip()
    {
        var live = DysonPlanResultFlow.CreateTurn(".dyson/plans/round-aabbccddee.md", "Round Trip");
        var entity = DysonTurnPersistence.ToEntity(live, Guid.NewGuid(), sequence: 3);
        if (entity.Kind != DysonAgentTurnKind.PlanResult
            || entity.PlanRelativePath != live.PlanRelativePath
            || entity.Instruction != live.Instruction
            || entity.AgentTitle != live.AgentTitle)
        {
            throw new InvalidOperationException("PlanResult ToEntity lost fields.");
        }

        var restored = new DysonAgentTurn
        {
            Id = entity.Id,
            Kind = entity.Kind,
            Instruction = entity.Instruction,
            AgentTitle = entity.AgentTitle,
            PlanRelativePath = entity.PlanRelativePath,
            AssistantText = entity.AssistantText,
            StartedUtc = entity.CreatedUtc,
            CompletedUtc = entity.CompletedUtc,
        };

        if (restored.Kind != DysonAgentTurnKind.PlanResult
            || restored.PlanRelativePath != ".dyson/plans/round-aabbccddee.md")
        {
            throw new InvalidOperationException("PlanResult restore field mismatch.");
        }
    }

    private static void AssertSubmitPlanCatalog()
    {
        var pipeline = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        if (!pipeline.Tools.TryGetValue("SubmitPlan", out var tool)
            || string.IsNullOrWhiteSpace(tool.Description)
            || !tool.InputSchemaJson.Contains("\"title\"", StringComparison.Ordinal)
            || !tool.InputSchemaJson.Contains("\"markdown\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SubmitPlan MCP catalog entry missing or incomplete.");
        }
    }

    private static void AssertApplyAgentModeRebuild()
    {
        var session = new StubSession(DysonAgentModes.Work);
        if (session.SystemPromptGeneration != 0)
            throw new InvalidOperationException("Expected SystemPromptGeneration 0 at create.");

        var same = session.ApplyAgentMode(DysonAgentModes.Work);
        if (same.IsError || session.SystemPromptGeneration != 0)
            throw new InvalidOperationException("Same-mode ApplyAgentMode must no-op without bump.");

        var workPrompt = session.SystemPrompt;
        var switched = session.ApplyAgentMode(DysonAgentModes.Plan, "models-block");
        if (switched.IsError
            || session.Mode != DysonAgentModes.Plan
            || session.SystemPromptGeneration != 1
            || session.SystemPrompt == workPrompt
            || !session.SystemPrompt.Contains("models-block", StringComparison.Ordinal)
            || !session.SystemPrompt.Contains("Plan", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ApplyAgentMode must rebuild prompt and bump generation.");
        }

        var key0 = OpenAiCompatibleHttp.PromptCacheKey(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), 0);
        var key1 = OpenAiCompatibleHttp.PromptCacheKey(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), 1);
        if (key0 == key1
            || !key1.EndsWith(":sp1", StringComparison.Ordinal)
            || !key0.EndsWith(":sp0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("PromptCacheKey must include system-prompt generation.");
        }
    }

    private static void AssertPlanReadyPending()
    {
        var plan = DysonPlanResultFlow.CreateTurn(".dyson/plans/sticky-aabbccddee.md", "Sticky Plan");
        var turns = new List<DysonAgentTurn> { plan };

        var pending = DysonPlanReadyUi.TryGetPending(turns);
        if (pending is null
            || pending.Path != ".dyson/plans/sticky-aabbccddee.md"
            || pending.Title != "Sticky Plan")
        {
            throw new InvalidOperationException("TryGetPending should surface latest PlanResult.");
        }

        turns.Add(new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "unrelated follow-up",
            StartedUtc = DateTime.UtcNow,
        });
        if (DysonPlanReadyUi.TryGetPending(turns) is null)
            throw new InvalidOperationException("Non-build user turn must keep sticky visible.");

        turns.Add(new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = DysonPlanResultFlow.BuildPlanUserPrompt(".dyson/plans/sticky-aabbccddee.md"),
            StartedUtc = DateTime.UtcNow,
        });
        if (DysonPlanReadyUi.TryGetPending(turns) is not null)
            throw new InvalidOperationException("Legacy BuildPlan marker turn must hide sticky.");

        var newer = DysonPlanResultFlow.CreateTurn(".dyson/plans/newer-ffeeddccbb.md", "Newer Plan");
        turns.Add(newer);
        var again = DysonPlanReadyUi.TryGetPending(turns);
        if (again is null || again.Path != ".dyson/plans/newer-ffeeddccbb.md")
            throw new InvalidOperationException("Newer PlanResult must re-show sticky.");

        turns.Add(DysonBeginBuildPlanFlow.CreateTurn(".dyson/plans/newer-ffeeddccbb.md"));
        if (DysonPlanReadyUi.TryGetPending(turns) is not null)
            throw new InvalidOperationException("BeginBuildPlan turn must hide sticky.");
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string agentMode) : DysonAgentSession(
        agentMode,
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
