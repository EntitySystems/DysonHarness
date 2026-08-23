using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: FullSummarize kind/flow — instruction, CreateTurn, after-completion drop + trim (Xunit Fact).
/// </summary>
public class DysonFullSummarizeTests
{
    [Fact]
    public void Run()
    {
        AssertKindAndDisplay();
        AssertInstruction();
        AssertCreateTurn();
        AssertShouldApplyAfterCompletion();
        AssertApplyDropsEarlierKeepsSelf();
        AssertApplyTrimsOverCap();
        AssertApplySkipsAlreadyExcluded();
        AssertNotTaskEndReflectionTrigger();
    }

    private static void AssertKindAndDisplay()
    {
        if ((int)DysonAgentTurnKind.FullSummarize != 16)
            throw new InvalidOperationException("DysonAgentTurnKind.FullSummarize must be 16.");

        var label = DysonAgentTurnKindDisplay.GetDisplayName(DysonAgentTurnKind.FullSummarize);
        if (!string.Equals(label, "Full summary", StringComparison.Ordinal))
            throw new InvalidOperationException($"FullSummarize label expected 'Full summary', got '{label}'.");
    }

    private static void AssertInstruction()
    {
        if (DysonFullSummarizeFlow.MaxSummaryCharacters != 6_000)
            throw new InvalidOperationException("MaxSummaryCharacters must be 6,000.");

        var instruction = DysonFullSummarizeFlow.Instruction;
        if (!instruction.Contains("6,000 characters", StringComparison.Ordinal)
            || !instruction.Contains("Do not call tools", StringComparison.Ordinal)
            || !instruction.Contains("exclude every previous turn", StringComparison.Ordinal)
            || !instruction.Contains("DropTurnContext", StringComparison.Ordinal)
            || !instruction.Contains("SummarizeTurns", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Instruction must mention 6,000 characters, no tools, and drop-after.");
        }
    }

    private static void AssertCreateTurn()
    {
        var turn = DysonFullSummarizeFlow.CreateTurn();
        if (turn.Kind != DysonAgentTurnKind.FullSummarize
            || turn.Instruction != DysonFullSummarizeFlow.Instruction
            || turn.CompletedUtc is not null)
        {
            throw new InvalidOperationException(
                "CreateTurn must set unfinished FullSummarize with the standard Instruction.");
        }

        var session = new StubSession();
        var viaSession = session.CreateFullSummarizeTurn();
        if (viaSession.Kind != DysonAgentTurnKind.FullSummarize
            || viaSession.Instruction != DysonFullSummarizeFlow.Instruction)
        {
            throw new InvalidOperationException(
                "CreateFullSummarizeTurn must match DysonFullSummarizeFlow.CreateTurn.");
        }
    }

    private static void AssertShouldApplyAfterCompletion()
    {
        if (!DysonFullSummarizeFlow.ShouldApplyAfterCompletion(DysonAgentTurnKind.FullSummarize)
            || DysonFullSummarizeFlow.ShouldApplyAfterCompletion(DysonAgentTurnKind.Normal)
            || DysonFullSummarizeFlow.ShouldApplyAfterCompletion(DysonAgentTurnKind.DropContext)
            || DysonFullSummarizeFlow.ShouldApplyAfterCompletion(DysonAgentTurnKind.ReportSummary))
        {
            throw new InvalidOperationException(
                "ShouldApplyAfterCompletion must be true only for FullSummarize.");
        }
    }

    private static void AssertApplyDropsEarlierKeepsSelf()
    {
        var session = new StubSession();
        var earlier = Completed(DysonAgentTurnKind.Normal, "keep facts");
        var alsoEarlier = Completed(DysonAgentTurnKind.ExpandThoughtProcess, "plan");
        var summary = DysonFullSummarizeFlow.CreateTurn();
        summary.AssistantText = "# Session summary: demo\n\nShort handoff.";
        summary.CompletedUtc = DateTime.UtcNow;

        session.AddTurnForTest(earlier);
        session.AddTurnForTest(alsoEarlier);
        session.AddTurnForTest(summary);

        var dropped = DysonFullSummarizeFlow.ApplyAfterCompletion(session, summary);
        if (dropped.Count != 2
            || !dropped.Contains(earlier)
            || !dropped.Contains(alsoEarlier))
        {
            throw new InvalidOperationException(
                "ApplyAfterCompletion must return the newly dropped earlier turns.");
        }

        if (!earlier.IsExcludedFromContext || !alsoEarlier.IsExcludedFromContext)
            throw new InvalidOperationException("ApplyAfterCompletion must exclude every earlier turn.");

        if (summary.IsExcludedFromContext)
            throw new InvalidOperationException("ApplyAfterCompletion must keep the FullSummarize turn.");

        var logEarlier = $"Turn {earlier.Id:D} dropped, reason: Full summarize";
        var logAlso = $"Turn {alsoEarlier.Id:D} dropped, reason: Full summarize";
        var logs = session.SnapshotLog();
        if (!logs.Any(l => l.Equals(logEarlier, StringComparison.Ordinal))
            || !logs.Any(l => l.Equals(logAlso, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Drop must AppendLog: Turn {id} dropped, reason: Full summarize");
        }
    }

    private static void AssertApplyTrimsOverCap()
    {
        var session = new StubSession();
        var earlier = Completed(DysonAgentTurnKind.Normal, "old");
        var summary = DysonFullSummarizeFlow.CreateTurn();
        summary.AssistantText = new string('x', DysonFullSummarizeFlow.MaxSummaryCharacters + 25);
        summary.CompletedUtc = DateTime.UtcNow;
        session.AddTurnForTest(earlier);
        session.AddTurnForTest(summary);

        DysonFullSummarizeFlow.ApplyAfterCompletion(session, summary);
        if (summary.AssistantText is null
            || summary.AssistantText.Length != DysonFullSummarizeFlow.MaxSummaryCharacters)
        {
            throw new InvalidOperationException(
                "ApplyAfterCompletion must hard-trim AssistantText over 6,000 characters.");
        }

        var exact = DysonFullSummarizeFlow.CreateTurn();
        exact.AssistantText = new string('y', DysonFullSummarizeFlow.MaxSummaryCharacters);
        DysonFullSummarizeFlow.ApplyAfterCompletion(session, exact);
        if (exact.AssistantText.Length != DysonFullSummarizeFlow.MaxSummaryCharacters)
            throw new InvalidOperationException("Text at the 6,000 cap must not be trimmed further.");
    }

    private static void AssertApplySkipsAlreadyExcluded()
    {
        var session = new StubSession();
        var already = Completed(DysonAgentTurnKind.Normal, "noise");
        already.IsExcludedFromContext = true;
        var live = Completed(DysonAgentTurnKind.Normal, "work");
        var summary = DysonFullSummarizeFlow.CreateTurn();
        summary.AssistantText = "# Session summary: skip\n\nDone.";
        summary.CompletedUtc = DateTime.UtcNow;

        session.AddTurnForTest(already);
        session.AddTurnForTest(live);
        session.AddTurnForTest(summary);

        var dropped = DysonFullSummarizeFlow.ApplyAfterCompletion(session, summary);
        if (dropped.Count != 1 || !ReferenceEquals(dropped[0], live))
            throw new InvalidOperationException("Already-excluded turns must not be returned again.");

        var alreadyLog = $"Turn {already.Id:D} dropped, reason: Full summarize";
        if (session.SnapshotLog().Any(l => l.Equals(alreadyLog, StringComparison.Ordinal)))
            throw new InvalidOperationException("Already-excluded turns must not be re-logged.");

        var again = DysonFullSummarizeFlow.ApplyAfterCompletion(session, summary);
        if (again.Count != 0)
            throw new InvalidOperationException("Second ApplyAfterCompletion must be a no-op.");
    }

    private static void AssertNotTaskEndReflectionTrigger()
    {
        if (DysonTaskLifecycleFlow.IsTaskEndReflectionTriggerKind(DysonAgentTurnKind.FullSummarize))
        {
            throw new InvalidOperationException(
                "FullSummarize must not be a TaskEndReflect trigger kind.");
        }
    }

    private static DysonAgentTurn Completed(DysonAgentTurnKind kind, string text) =>
        new()
        {
            Kind = kind,
            Instruction = text,
            AssistantText = text,
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
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
