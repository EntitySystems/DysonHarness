using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Plan first-turn Explore mandate + chrome-skipped rename-review slots on
/// Completions and Responses transcripts (ephemeral, not stored on Instruction).
/// </summary>
public class DysonPlanFirstTurnMandateTests
{
    private const string PlanNeedle = "StartSubagent at least one Explore";
    private const string RenameNeedle = "Decide whether to rename this session";

    [Fact]
    public void Run()
    {
        AssertPlanMandatePresentCleanFirstTurn();
        AssertPlanMandatePresentAfterWorkToPlanSwitch();
        AssertPlanMandatePresentAfterDisplayInfoAndModeSwitch();
        AssertPlanMandatePresentAfterMidSessionWorkSwitch();
        AssertPlanMandateAbsentOnSecondPlanTurn();
        AssertPlanMandateAbsentOnWorkFirstTurn();
        AssertPlanMandateAbsentOnAskFirstTurn();
        AssertPlanMandateAbsentOnCompletedFirstPlanTurn();
        AssertRenameReviewPresentCleanWorkFirstTurn();
        AssertRenameReviewPresentAskCta();
        AssertRenameReviewAndPlanMandatePresentPickerSwitch();
        AssertRenameReviewAbsentOnSecondEligibleTurn();
        AssertRenameReviewAbsentOnCompletedFirstTurn();
        AssertStartSubagentCatalogWording();
    }

    private static void AssertPlanMandatePresentCleanFirstTurn()
    {
        var session = new StubSession(DysonAgentModes.Plan);
        session.AddTurnForTest(DysonAgentSession.CreateNormalTurn("map the engine"));
        AssertBoth(session, json =>
        {
            MustContain(json, PlanNeedle, "Plan first turn");
            MustContain(json, DysonAgentSystemPrompts.PlanFirstTurnMandate.Split('\n')[0].Trim(), "Plan first turn heading");
        });
    }

    private static void AssertPlanMandatePresentAfterWorkToPlanSwitch()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var applied = session.ApplyAgentMode(DysonAgentModes.Plan);
        if (applied.IsError)
            throw new InvalidOperationException($"ApplyAgentMode(Plan) failed: {applied.Error}");

        session.AppendModeSwitchTurn(DysonAgentModes.Work, DysonAgentModes.Plan);
        session.AddTurnForTest(DysonAgentSession.CreateNormalTurn("plan the feature"));
        AssertBoth(session, json => MustContain(json, PlanNeedle, "Work→Plan first prompt"));
    }

    private static void AssertPlanMandatePresentAfterDisplayInfoAndModeSwitch()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var applied = session.ApplyAgentMode(DysonAgentModes.Plan);
        if (applied.IsError)
            throw new InvalidOperationException($"ApplyAgentMode(Plan) failed: {applied.Error}");

        session.AppendDisplayInfoTurn("Create a plan");
        session.AppendModeSwitchTurn(DysonAgentModes.Work, DysonAgentModes.Plan);
        session.AddTurnForTest(DysonAgentSession.CreateNormalTurn("design the API"));
        AssertBoth(session, json => MustContain(json, PlanNeedle, "Create-plan CTA"));
    }

    private static void AssertPlanMandatePresentAfterMidSessionWorkSwitch()
    {
        var session = new StubSession(DysonAgentModes.Work);
        session.AddTurnForTest(Completed(DysonAgentSession.CreateNormalTurn("prior work one")));
        session.AddTurnForTest(Completed(DysonAgentSession.CreateNormalTurn("prior work two")));
        var applied = session.ApplyAgentMode(DysonAgentModes.Plan);
        if (applied.IsError)
            throw new InvalidOperationException($"ApplyAgentMode(Plan) failed: {applied.Error}");

        session.AppendModeSwitchTurn(DysonAgentModes.Work, DysonAgentModes.Plan);
        session.AddTurnForTest(DysonAgentSession.CreateNormalTurn("now plan the rewrite"));
        AssertBoth(session, json => MustContain(json, PlanNeedle, "mid-session Work→Plan"));
    }

    private static void AssertPlanMandateAbsentOnSecondPlanTurn()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var applied = session.ApplyAgentMode(DysonAgentModes.Plan);
        if (applied.IsError)
            throw new InvalidOperationException($"ApplyAgentMode(Plan) failed: {applied.Error}");

        session.AppendModeSwitchTurn(DysonAgentModes.Work, DysonAgentModes.Plan);
        session.AddTurnForTest(Completed(DysonAgentSession.CreateNormalTurn("first plan prompt")));
        session.AddTurnForTest(DysonAgentSession.CreateNormalTurn("second plan prompt"));
        AssertBoth(session, json => MustNotContain(json, PlanNeedle, "second Plan prompt"));
    }

    private static void AssertPlanMandateAbsentOnWorkFirstTurn()
    {
        var session = new StubSession(DysonAgentModes.Work);
        session.AddTurnForTest(DysonAgentSession.CreateNormalTurn("implement the fix"));
        AssertBoth(session, json => MustNotContain(json, PlanNeedle, "Work first turn"));
    }

    private static void AssertPlanMandateAbsentOnAskFirstTurn()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var applied = session.ApplyAgentMode(DysonAgentModes.Ask);
        if (applied.IsError)
            throw new InvalidOperationException($"ApplyAgentMode(Ask) failed: {applied.Error}");

        session.AppendModeSwitchTurn(DysonAgentModes.Work, DysonAgentModes.Ask);
        session.AppendDisplayInfoTurn("Ask me anything");
        session.AddTurnForTest(DysonAgentSession.CreateNormalTurn("what does ModeSwitch do?"));
        AssertBoth(session, json => MustNotContain(json, PlanNeedle, "Ask first turn"));
    }

    private static void AssertPlanMandateAbsentOnCompletedFirstPlanTurn()
    {
        var session = new StubSession(DysonAgentModes.Plan);
        session.AddTurnForTest(Completed(DysonAgentSession.CreateNormalTurn("already planned")));
        AssertBoth(session, json => MustNotContain(json, PlanNeedle, "completed first Plan turn"));
    }

    private static void AssertRenameReviewPresentCleanWorkFirstTurn()
    {
        var session = new StubSession(DysonAgentModes.Work);
        session.AddTurnForTest(DysonSessionInitialization.CreateTurn("scan the tree"));
        AssertBoth(session, json =>
        {
            MustContain(json, RenameNeedle, "clean Work InitializeSession");
            MustContain(json, DysonSessionInitialization.RenameSessionReviewMandate.Split('\n')[0].Trim(), "rename heading");
        });
    }

    private static void AssertRenameReviewPresentAskCta()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var applied = session.ApplyAgentMode(DysonAgentModes.Ask);
        if (applied.IsError)
            throw new InvalidOperationException($"ApplyAgentMode(Ask) failed: {applied.Error}");

        session.AppendModeSwitchTurn(DysonAgentModes.Work, DysonAgentModes.Ask);
        session.AppendDisplayInfoTurn("Ask me anything");
        session.AddTurnForTest(DysonAgentSession.CreateNormalTurn("explain the picker"));
        AssertBoth(session, json => MustContain(json, RenameNeedle, "Ask CTA rename slot 1"));
    }

    private static void AssertRenameReviewAndPlanMandatePresentPickerSwitch()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var applied = session.ApplyAgentMode(DysonAgentModes.Plan);
        if (applied.IsError)
            throw new InvalidOperationException($"ApplyAgentMode(Plan) failed: {applied.Error}");

        session.AppendModeSwitchTurn(DysonAgentModes.Work, DysonAgentModes.Plan);
        session.AddTurnForTest(DysonAgentSession.CreateNormalTurn("plan after picker switch"));
        AssertBoth(session, json =>
        {
            MustContain(json, PlanNeedle, "picker Plan Explore mandate");
            MustContain(json, RenameNeedle, "picker Plan rename slot 1");
        });
    }

    private static void AssertRenameReviewAbsentOnSecondEligibleTurn()
    {
        var session = new StubSession(DysonAgentModes.Work);
        session.AppendModeSwitchTurn(DysonAgentModes.Work, DysonAgentModes.Ask);
        session.AppendDisplayInfoTurn("Ask me anything");
        session.AddTurnForTest(Completed(DysonAgentSession.CreateNormalTurn("first eligible")));
        session.AddTurnForTest(DysonAgentSession.CreateNormalTurn("second eligible"));
        AssertBoth(session, json => MustNotContain(json, RenameNeedle, "eligible slot 2"));
    }

    private static void AssertRenameReviewAbsentOnCompletedFirstTurn()
    {
        var session = new StubSession(DysonAgentModes.Work);
        session.AddTurnForTest(Completed(DysonAgentSession.CreateNormalTurn("already answered")));
        AssertBoth(session, json => MustNotContain(json, RenameNeedle, "completed first turn"));
    }

    private static void AssertStartSubagentCatalogWording()
    {
        var pipeline = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        if (!pipeline.Tools.TryGetValue("StartSubagent", out var tool))
            throw new InvalidOperationException("StartSubagent must be in the FullAccess catalog.");

        if (!tool.Description.Contains("Plan parent may StartSubagent Explore", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "StartSubagent description must contain 'Plan parent may StartSubagent Explore'.");
        }

        if (tool.Description.Contains("Plan is banned as a subagent mode", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "StartSubagent description must not use the old 'Plan is banned as a subagent mode' phrase.");
        }
    }

    private static void AssertBoth(StubSession session, Action<string> assertJson)
    {
        var completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: []);
        assertJson(completions.Messages.ToJsonString());

        var responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: []);
        assertJson(responses.Input.ToJsonString());
    }

    private static void MustContain(string json, string needle, string label)
    {
        if (!json.Contains(needle, StringComparison.Ordinal))
            throw new InvalidOperationException($"{label}: transcript must contain '{needle}'.");
    }

    private static void MustNotContain(string json, string needle, string label)
    {
        if (json.Contains(needle, StringComparison.Ordinal))
            throw new InvalidOperationException($"{label}: transcript must not contain '{needle}'.");
    }

    private static DysonAgentTurn Completed(DysonAgentTurn turn)
    {
        turn.AssistantText = "done";
        turn.CompletedUtc = DateTime.UtcNow;
        return turn;
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode) : DysonAgentSession(
        mode,
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
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> LoadFunctionalContextAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

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
            => Task.FromResult(VoidResult<string>.Success);

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
