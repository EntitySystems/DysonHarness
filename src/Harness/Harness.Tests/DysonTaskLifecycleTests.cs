using System.Reflection;
using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Automatic code-review task lifecycle: append-only turn kinds, factories, level
/// normalization, ReportSummary timing, and (when the engine API lands) gates/dedup.
/// </summary>
/// <remarks>
/// <para>
/// Implementation lives in <c>DysonTaskLifecycleFlow</c> + session evaluation + host
/// event handling. This file stays compile-safe before those types exist; once
/// <c>TaskEndReflect</c>/<c>BugReview</c> are appended, factory and prompt assertions
/// run automatically. Host queue/gate coverage is listed in the matrix below and
/// belongs in <c>DysonUiHostTaskLifecycleTests</c> after the host drone lands
/// (do not edit <c>DysonUiHost</c> / <c>AgentBehavior</c> from this workstream).
/// </para>
/// <para>
/// Test matrix (seams):
/// <list type="number">
/// <item>
/// <b>Kinds / display</b> — <c>DysonAgentTurnKind.TaskEndReflect=14</c>,
/// <c>BugReview=15</c> (append only; never renumber 0–13). Labels
/// "Task end reflection" / "Code review". Also
/// <c>DysonAgentTurnKindDisplayTests</c>.
/// </item>
/// <item>
/// <b>Factories</b> — <c>DysonTaskLifecycleFlow.CreateTaskEndReflectTurn()</c>,
/// <c>CreateBugReviewTurn(level)</c>. Session helpers parallel
/// <c>DysonAgentSession.CreateReportSummaryTurn</c>
/// (<c>src/Harness/Harness.Engine/Session/DysonAgentSession.cs</c> ~1945).
/// </item>
/// <item>
/// <b>Prompts</b> — reflect: prior turn done, no running subagents, pending/ongoing
/// todos, verify remaining work, update todo status, do not declare success.
/// Bug review: exactly one Bug Review child, omit <c>modelSlug</c>,
/// <c>WaitForSubagent</c> this turn, evidence-based report, no auto-fix.
/// Low = modified files + behavior. Medium = those plus related APIs / external
/// interactions + thorough wording.
/// </item>
/// <item>
/// <b>Normalize</b> — <c>none</c>/<c>low</c>/<c>medium</c>/<c>high</c>
/// (case-insensitive). High is display-only / unsupported (must not start a
/// review). Legacy: new key absent + <c>end_of_task_auto_review</c> false/missing
/// → none; true + <c>self_review_intensity</c> low → low; true + medium/other →
/// medium. Subsequent saves use <c>automatic_code_review</c> only.
/// </item>
/// <item>
/// <b>Evaluate gates</b> (session, after a completed root turn): root only;
/// non-empty todos; no pending session follow-ups; no host-queued follow-up
/// (<c>hasQueuedFollowUp</c>); no in-flight prompt; no
/// active descendant (host flag or engine walk of <c>SubSessions</c>;
/// the host flag is not the only descendant source);
/// relevant turns finalized. Empty todos never auto-review chat.
/// </item>
/// <item>
/// <b>TaskEndReflectionRequired</b> — incomplete todos after an eligible
/// substantive turn. Do not retrigger from <c>TaskEndReflect</c>,
/// <c>BugReview</c>, <c>TaskCompletionConfirm</c>, or other pure
/// lifecycle/finalization kinds (exactly-once / no spin).
/// </item>
/// <item>
/// <b>CodeReviewReady</b> — all todos Complete AND last boundary is
/// <c>ReportSummary</c>. Not merely because a todo flipped complete mid-task.
/// Preserve CompleteTask → confirm → report chain
/// (<c>DysonTaskCompletionTests</c>).
/// </item>
/// <item>
/// <b>Dedup</b> — a recorded <c>BugReview</c> turn means review already
/// started; do not enqueue a second. Derive from turn history (no new column).
/// Re-evaluate restored active roots after registration.
/// </item>
/// <item>
/// <b>ReadyToFinalize</b> — after the BugReview orchestration turn completed
/// and its reviewer is terminal. <c>none</c> / unsupported <c>high</c> finalize
/// immediately on CodeReviewReady (no review turn).
/// </item>
/// <item>
/// <b>Host</b> — <c>DysonUiHost.ExecutePromptOnSessionAsync</c> after persist +
/// <c>TryDequeuePendingTurn</c> drain (~4674–4692) must invoke the evaluator
/// instead of <c>ShouldMarkTerminalAfterTurn</c> → <c>PersistRootTerminalAsync</c>.
/// Also after child completion/report and restore <c>EnsureRegistered</c> (~3710).
/// Unsubscribe in <c>UnhookSession</c> (~3801). Queue via <c>EnqueuePrompt</c>
/// (~4720) / <c>DrainQueuedPromptsAsync</c> under <c>_promptGates</c> /
/// <c>_autoTurnGates</c>. Settings: <c>AgentBehavior.razor</c> +
/// <c>DysonAppSettingKeys.AutomaticCodeReview</c>. Host test file (after API):
/// <c>DysonUiHostTaskLifecycleTests</c> using <c>DysonTempDb</c> like
/// <c>DysonUiHostDeferredModelSwitchTests</c>.
/// </item>
/// <item>
/// <b>modelSlug omission</b> — Bug Review prompt must omit
/// <c>StartSubagent.modelSlug</c>. Default resolution stays in
/// <c>DysonSubagentModelDefaultTests</c> +
/// <c>DysonAgentSessionConfig.BugReviewDefaultProvider</c>.
/// </item>
/// </list>
/// </para>
/// </remarks>
public class DysonTaskLifecycleTests
{
    private const int LastPreLifecycleKindValue = 13; // DropContext
    private const int TaskEndReflectValue = 14;
    private const int BugReviewValue = 15;
    private const int FullSummarizeValue = 16;
    private const int WorktreeCreatingValue = 17;

    [Fact]
    public async Task Run()
    {
        AssertAppendOnlyKindContract();
        AssertLegacySettingKeysRemainReadable();
        await AssertCompletionChainStillStopsAtReportSummaryEnqueue();
        AssertBugReviewDefaultUsesOmittedModelSlug();
        AssertHasActiveDescendantIsHostAuthority();

        if (!TryGetLifecycleKinds(out var reflect, out var bugReview))
            return;

        AssertLifecycleKindValuesAndLabels(reflect, bugReview);
        AssertLifecycleFlowWhenPresent(reflect, bugReview);
        AssertActionAwareBugReviewPrompts();
        AssertEvaluationGatesAndDedup(reflect, bugReview);
        AssertLastTurnKeysCompletionBoundary();
        AssertChildReflectionGate(reflect);
        AssertAutomaticReviewCompletionSuppression();
    }

    private static void AssertAppendOnlyKindContract()
    {
        if ((int)DysonAgentTurnKind.Normal != 0
            || (int)DysonAgentTurnKind.ExpandThoughtProcess != 1
            || (int)DysonAgentTurnKind.TaskCompletionConfirm != 2
            || (int)DysonAgentTurnKind.Continuation != 3
            || (int)DysonAgentTurnKind.ReportSummary != 4
            || (int)DysonAgentTurnKind.InitializeSession != 5
            || (int)DysonAgentTurnKind.PlanResult != 6
            || (int)DysonAgentTurnKind.BeginBuildPlan != 7
            || (int)DysonAgentTurnKind.SubagentReportProcessing != 8
            || (int)DysonAgentTurnKind.ShellExited != 9
            || (int)DysonAgentTurnKind.RethinkToolUsage != 10
            || (int)DysonAgentTurnKind.DisplayInfo != 11
            || (int)DysonAgentTurnKind.ModeSwitch != 12
            || (int)DysonAgentTurnKind.DropContext != LastPreLifecycleKindValue)
        {
            throw new InvalidOperationException(
                "DysonAgentTurnKind values 0–13 must stay stable; append TaskEndReflect=14, BugReview=15, FullSummarize=16, WorktreeCreating=17.");
        }

        var max = Enum.GetValues<DysonAgentTurnKind>().Select(k => (int)k).Max();
        if (max < LastPreLifecycleKindValue)
            throw new InvalidOperationException("DysonAgentTurnKind lost DropContext=13.");
        if (max > WorktreeCreatingValue)
        {
            throw new InvalidOperationException(
                $"Unexpected DysonAgentTurnKind value {max}; expected append-only through WorktreeCreating=17.");
        }
    }

    private static void AssertLegacySettingKeysRemainReadable()
    {
        if (!string.Equals(DysonAppSettingKeys.EndOfTaskAutoReview, "end_of_task_auto_review", StringComparison.Ordinal)
            || !string.Equals(DysonAppSettingKeys.SelfReviewIntensity, "self_review_intensity", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Legacy EndOfTaskAutoReview / SelfReviewIntensity key strings must remain for one-shot migration.");
        }
    }

    private static async Task AssertCompletionChainStillStopsAtReportSummaryEnqueue()
    {
        // CompleteTask → confirm → ConfirmTaskComplete still only enqueues ReportSummary.
        // Host terminal persist must move to the lifecycle evaluator (CodeReviewReady /
        // ReadyToFinalize), not fire unconditionally from this enqueue.
        var session = new StubSession();
        session.ConfigureRootForTest();
        session.AddTurnForTest(DysonTaskCompletionFlow.CreateCompletionConfirmTurn("prior"));
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, Path.GetTempPath(), http);

        var result = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "lc-confirm",
            ToolName = "ConfirmTaskComplete",
            Stage = 0,
            ArgumentsJson = """{"rationale":"verified"}""",
        });

        if (result.IsError)
            throw new InvalidOperationException("ConfirmTaskComplete should succeed: " + result.Content);

        if (!session.TryDequeuePendingTurn(out var turn)
            || turn.Kind != DysonAgentTurnKind.ReportSummary)
        {
            throw new InvalidOperationException(
                "ConfirmTaskComplete must still enqueue ReportSummary (lifecycle starts after that turn).");
        }

        if (session.IsTerminal)
        {
            throw new InvalidOperationException(
                "ReportSummary enqueue must not mark the session terminal; that is a host lifecycle decision.");
        }

        if (!DysonTaskCompletionFlow.ShouldMarkTerminalAfterTurn(DysonAgentTurnKind.ReportSummary))
        {
            throw new InvalidOperationException(
                "ShouldMarkTerminalAfterTurn(ReportSummary) remains the completion-boundary predicate; "
                + "the host must not treat it as an unconditional PersistRootTerminal.");
        }

        if (DysonTaskCompletionFlow.ShouldMarkTerminalAfterTurn(DysonAgentTurnKind.TaskCompletionConfirm)
            || DysonTaskCompletionFlow.ShouldMarkTerminalAfterTurn(DysonAgentTurnKind.Continuation)
            || DysonTaskCompletionFlow.ShouldMarkTerminalAfterTurn(DysonAgentTurnKind.Normal))
        {
            throw new InvalidOperationException(
                "ShouldMarkTerminalAfterTurn must stay false for confirm / continuation / normal.");
        }
    }

    private static void AssertBugReviewDefaultUsesOmittedModelSlug()
    {
        var bug = new OpenAiCompatibleAgentProvider(
            new DysonModelSlugEntity { Slug = "bug-default", DisplayAlias = "Bug" });
        var config = new DysonAgentSessionConfig { BugReviewDefaultProvider = bug };

        if (!ReferenceEquals(
                config.TryGetSubagentDefaultWhenSlugOmitted(null, DysonAgentModes.BugReview),
                bug))
        {
            throw new InvalidOperationException(
                "Omitting StartSubagent.modelSlug for Bug Review must resolve BugReviewDefaultProvider.");
        }

        if (config.TryGetSubagentDefaultWhenSlugOmitted("explicit-slug", DysonAgentModes.BugReview) is not null)
        {
            throw new InvalidOperationException(
                "An explicit modelSlug must win; the lifecycle prompt must therefore omit modelSlug.");
        }
    }

    private static void AssertHasActiveDescendantIsHostAuthority()
    {
        var root = new StubSession();
        if (Harness.UI.Demo.DysonSubagentHostLogic.HasActiveDescendant(root))
            throw new InvalidOperationException("Empty root must not report an active descendant.");
    }

    private static bool TryGetLifecycleKinds(out DysonAgentTurnKind reflect, out DysonAgentTurnKind bugReview)
    {
        var hasReflect = Enum.TryParse("TaskEndReflect", ignoreCase: false, out reflect);
        var hasBug = Enum.TryParse("BugReview", ignoreCase: false, out bugReview);
        if (hasReflect != hasBug)
        {
            throw new InvalidOperationException(
                "TaskEndReflect and BugReview must be appended together (14 then 15).");
        }

        return hasReflect && hasBug;
    }

    private static void AssertLifecycleKindValuesAndLabels(
        DysonAgentTurnKind reflect,
        DysonAgentTurnKind bugReview)
    {
        if ((int)reflect != TaskEndReflectValue)
            throw new InvalidOperationException("DysonAgentTurnKind.TaskEndReflect must be 14.");
        if ((int)bugReview != BugReviewValue)
            throw new InvalidOperationException("DysonAgentTurnKind.BugReview must be 15.");

        var reflectLabel = DysonAgentTurnKindDisplay.GetDisplayName(reflect);
        if (!string.Equals(reflectLabel, "Task end reflection", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"TaskEndReflect label expected 'Task end reflection', got '{reflectLabel}'.");
        }

        var bugLabel = DysonAgentTurnKindDisplay.GetDisplayName(bugReview);
        if (!string.Equals(bugLabel, "Code review", StringComparison.Ordinal))
            throw new InvalidOperationException($"BugReview label expected 'Code review', got '{bugLabel}'.");
    }

    private static void AssertLifecycleFlowWhenPresent(
        DysonAgentTurnKind reflect,
        DysonAgentTurnKind bugReview)
    {
        var flowType = typeof(DysonTaskCompletionFlow).Assembly.GetType(
            "DysonHarness.DysonTaskLifecycleFlow");
        if (flowType is null)
        {
            throw new InvalidOperationException(
                "TaskEndReflect/BugReview landed without DysonTaskLifecycleFlow; add the flow + factories.");
        }

        var reflectTurn = InvokeTurnFactory(flowType, "CreateTaskEndReflectTurn");
        if (reflectTurn.Kind != reflect || string.IsNullOrWhiteSpace(reflectTurn.Instruction))
            throw new InvalidOperationException("CreateTaskEndReflectTurn must set TaskEndReflect + instruction.");

        AssertContainsAll(
            reflectTurn.Instruction,
            "CreateTaskEndReflectTurn",
            ["todo", "pending", "subagent"]);
        if (reflectTurn.Instruction.Contains("StartSubagent", StringComparison.Ordinal)
            && reflectTurn.Instruction.Contains("Bug Review", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "TaskEndReflect must not spawn the Bug Review child (that is the BugReview turn).");
        }

        var low = InvokeBugReviewFactory(flowType, "low");
        var medium = InvokeBugReviewFactory(flowType, "medium");
        if (low.Kind != bugReview || medium.Kind != bugReview)
            throw new InvalidOperationException("CreateBugReviewTurn must set BugReview kind.");

        AssertBugReviewSharedContract(low.Instruction!, "low");
        AssertBugReviewSharedContract(medium.Instruction!, "medium");

        if (string.Equals(low.Instruction, medium.Instruction, StringComparison.Ordinal))
            throw new InvalidOperationException("Low and medium BugReview prompts must differ.");

        AssertContainsAll(low.Instruction!, "low BugReview prompt", ["modified"]);
        AssertContainsAll(medium.Instruction!, "medium BugReview prompt", ["api", "thorough"]);

        AssertNormalize(flowType);
        AssertHighDoesNotStartReview(flowType);
    }

    private static void AssertActionAwareBugReviewPrompts()
    {
        if (DysonTaskLifecycleFlow.NormalizeReviewAction(null)
                != DysonAutomaticCodeReviewAction.ReportOnly
            || DysonTaskLifecycleFlow.NormalizeReviewAction("unexpected")
                != DysonAutomaticCodeReviewAction.ReportOnly
            || DysonTaskLifecycleFlow.NormalizeReviewAction("automatically_fix")
                != DysonAutomaticCodeReviewAction.AutomaticallyFix)
        {
            throw new InvalidOperationException("Automatic review action normalization mismatch.");
        }

        var reportOnly = DysonTaskLifecycleFlow.CreateBugReviewTurn(
            DysonAutomaticCodeReviewLevel.Low,
            DysonAutomaticCodeReviewAction.ReportOnly,
            "- Modified: src/Harness/Example.cs");
        AssertContainsAll(
            reportOnly.Instruction!,
            "report-only review prompt",
            ["report only", "Do not modify", "Example.cs", "confirmed bugs", "risks"]);

        var automaticallyFix = DysonTaskLifecycleFlow.CreateBugReviewTurn(
            DysonAutomaticCodeReviewLevel.Medium,
            DysonAutomaticCodeReviewAction.AutomaticallyFix,
            "Diagnostic: git status failed; determine review scope directly.");
        AssertContainsAll(
            automaticallyFix.Instruction!,
            "automatically-fix review prompt",
            ["automatically fix", "reviewer remains review-only", "validate", "fix only confirmed", "unresolved", "unlimited"]);
    }

    private static void AssertEvaluationGatesAndDedup(
        DysonAgentTurnKind reflect,
        DysonAgentTurnKind bugReview)
    {
        var session = new StubSession();
        DysonTaskLifecycleKind? raised = null;
        session.TaskLifecycle += (_, e) => raised = e.Kind;

        ExpectNoLifecycle(session.EvaluateTaskLifecycle(hasActiveDescendant: false), "empty todos");

        var todo = session.CreateTodoAsync("t1", "One").GetAwaiter().GetResult();
        if (todo.IsError)
            throw new InvalidOperationException("CreateTodo failed: " + todo.Error);

        ExpectNoLifecycle(session.EvaluateTaskLifecycle(hasActiveDescendant: false), "no turns");

        session.AddTurnForTest(Completed(DysonAgentSession.CreateNormalTurn("work")));
        var reflectDecision = session.EvaluateTaskLifecycle(hasActiveDescendant: false);
        if (reflectDecision.Kind != DysonTaskLifecycleKind.TaskEndReflectionRequired
            || raised != reflectDecision.Kind)
        {
            throw new InvalidOperationException(
                "Incomplete todos after Normal should raise TaskEndReflectionRequired.");
        }

        raised = null;
        session.AddTurnForTest(Completed(DysonTaskLifecycleFlow.CreateTaskEndReflectTurn()));
        ExpectNoLifecycle(session.EvaluateTaskLifecycle(hasActiveDescendant: false), "exactly-once reflection");

        session.AddTurnForTest(Completed(DysonAgentSession.CreateNormalTurn("more work")));
        var secondReflection = session.EvaluateTaskLifecycle(hasActiveDescendant: false);
        if (secondReflection.Kind != DysonTaskLifecycleKind.TaskEndReflectionRequired)
        {
            throw new InvalidOperationException(
                "A later substantive turn with incomplete todos should trigger another reflection.");
        }

        var child = new StubSession();
        session.RegisterForTest(child);
        ExpectNoLifecycle(child.EvaluateTaskLifecycle(hasActiveDescendant: false), "child is not root");

        var isolated = CreateRootWithCompletedTodoAndReport();
        ExpectNoLifecycle(isolated.EvaluateTaskLifecycle(hasActiveDescendant: true), "active descendant");

        isolated.EnqueuePendingTurn(DysonAgentSession.CreateNormalTurn("queued"));
        ExpectNoLifecycle(isolated.EvaluateTaskLifecycle(hasActiveDescendant: false), "pending follow-up");
        if (!isolated.HasPendingTurn || !isolated.TryDequeuePendingTurn(out _))
            throw new InvalidOperationException("Expected HasPendingTurn then a successful dequeue.");

        var queued = new StubSession();
        if (queued.CreateTodoAsync("queued-follow-up", "Still open").GetAwaiter().GetResult().IsError)
            throw new InvalidOperationException("Failed to seed queued-follow-up todo.");
        queued.AddTurnForTest(Completed(DysonAgentSession.CreateNormalTurn("work")));
        ExpectNoLifecycle(
            queued.EvaluateTaskLifecycle(hasActiveDescendant: false, hasQueuedFollowUp: true),
            "host-queued follow-up");

        var withChild = new StubSession();
        if (withChild.CreateTodoAsync("with-child", "Still open").GetAwaiter().GetResult().IsError)
            throw new InvalidOperationException("Failed to seed with-child todo.");
        withChild.AddTurnForTest(Completed(DysonAgentSession.CreateNormalTurn("work")));
        withChild.RegisterForTest(new StubSession());
        ExpectNoLifecycle(
            withChild.EvaluateTaskLifecycle(hasActiveDescendant: false),
            "engine walk of Active child");

        var inFlightTurn = Completed(DysonTaskCompletionFlow.CreateReportSummaryTurn("ok"));
        isolated.AddTurnForTest(inFlightTurn);
        using (isolated.BeginInFlightPrompt(inFlightTurn))
            ExpectNoLifecycle(isolated.EvaluateTaskLifecycle(hasActiveDescendant: false), "in-flight prompt");

        var reviewReady = CreateRootWithCompletedTodoAndReport();
        var ready = reviewReady.EvaluateTaskLifecycle(hasActiveDescendant: false);
        if (ready.Kind != DysonTaskLifecycleKind.CodeReviewReady)
        {
            throw new InvalidOperationException(
                "ReportSummary + complete todos should raise CodeReviewReady.");
        }

        var midTask = new StubSession();
        if (midTask.CreateTodoAsync("done", "Done", DysonSessionTodoStatus.Complete)
                .GetAwaiter()
                .GetResult()
                .IsError)
        {
            throw new InvalidOperationException("Failed to seed complete todo.");
        }

        midTask.AddTurnForTest(Completed(DysonAgentSession.CreateNormalTurn("mid")));
        ExpectNoLifecycle(
            midTask.EvaluateTaskLifecycle(hasActiveDescendant: false),
            "complete todos mid-task (no ReportSummary)");

        var afterReview = CreateRootWithCompletedTodoAndReport();
        afterReview.AddTurnForTest(DysonTaskLifecycleFlow.CreateBugReviewTurn(DysonAutomaticCodeReviewLevel.Low));
        ExpectNoLifecycle(
            afterReview.EvaluateTaskLifecycle(hasActiveDescendant: false),
            "BugReview started but not completed");
        ExpectNoLifecycle(
            afterReview.EvaluateTaskLifecycle(hasActiveDescendant: true),
            "reviewer still active");

        afterReview.Turns[^1].CompletedUtc = DateTime.UtcNow;
        var finalize = afterReview.EvaluateTaskLifecycle(hasActiveDescendant: false);
        if (finalize.Kind != DysonTaskLifecycleKind.ReadyToFinalize)
            throw new InvalidOperationException("Completed BugReview should raise ReadyToFinalize.");

        var reviewWithFollowUp = CreateRootWithCompletedTodoAndReport();
        reviewWithFollowUp.AddTurnForTest(Completed(
            DysonTaskLifecycleFlow.CreateBugReviewTurn(DysonAutomaticCodeReviewLevel.Medium)));
        var followUp = reviewWithFollowUp
            .CreateTodoAsync("review-follow-up", "Resolve confirmed review finding", DysonSessionTodoStatus.Ongoing)
            .GetAwaiter()
            .GetResult();
        if (followUp.IsError)
            throw new InvalidOperationException("Failed to add review follow-up todo: " + followUp.Error);

        ExpectNoLifecycle(
            reviewWithFollowUp.EvaluateTaskLifecycle(hasActiveDescendant: false),
            "incomplete todo introduced by automatic review");

        var confirmOnly = new StubSession();
        if (confirmOnly.CreateTodoAsync("open", "Open").GetAwaiter().GetResult().IsError)
            throw new InvalidOperationException("Failed to seed open todo.");
        confirmOnly.AddTurnForTest(Completed(DysonTaskCompletionFlow.CreateCompletionConfirmTurn("x")));
        ExpectNoLifecycle(
            confirmOnly.EvaluateTaskLifecycle(hasActiveDescendant: false),
            "confirm turn is not a reflection trigger");

        if (!DysonTaskLifecycleFlow.IsTaskEndReflectionTriggerKind(DysonAgentTurnKind.Normal)
            || DysonTaskLifecycleFlow.IsTaskEndReflectionTriggerKind(reflect)
            || DysonTaskLifecycleFlow.IsTaskEndReflectionTriggerKind(bugReview)
            || DysonTaskLifecycleFlow.IsTaskEndReflectionTriggerKind(DysonAgentTurnKind.TaskCompletionConfirm))
        {
            throw new InvalidOperationException("Reflection trigger kind set mismatch.");
        }

        var viaSession = new StubSession();
        var createdReflect = viaSession.CreateTaskEndReflectTurn();
        var createdBug = viaSession.CreateBugReviewTurn(DysonAutomaticCodeReviewLevel.Medium);
        if (createdReflect.Kind != reflect || createdBug.Kind != bugReview)
            throw new InvalidOperationException("Session lifecycle turn helpers mismatch.");
    }

    private static void AssertLastTurnKeysCompletionBoundary()
    {
        var session = CreateRootWithCompletedTodoAndReport();
        var reviewReady = session.EvaluateTaskLifecycle(hasActiveDescendant: false);
        if (reviewReady.Kind != DysonTaskLifecycleKind.CodeReviewReady)
        {
            throw new InvalidOperationException(
                "ReportSummary + complete todos (no BugReview) should raise CodeReviewReady.");
        }

        session.AddTurnForTest(Completed(
            DysonTaskLifecycleFlow.CreateBugReviewTurn(DysonAutomaticCodeReviewLevel.Low)));
        var finalize = session.EvaluateTaskLifecycle(hasActiveDescendant: false);
        if (finalize.Kind != DysonTaskLifecycleKind.ReadyToFinalize)
            throw new InvalidOperationException("Completed BugReview as last turn should raise ReadyToFinalize.");

        session.AddTurnForTest(Completed(DysonAgentSession.CreateNormalTurn("reopened user prompt")));
        ExpectNoLifecycle(
            session.EvaluateTaskLifecycle(hasActiveDescendant: false),
            "completed Normal after BugReview must not re-finalize");

        session.AddTurnForTest(Completed(DysonTaskCompletionFlow.CreateReportSummaryTurn("second cycle")));
        var secondCycle = session.EvaluateTaskLifecycle(hasActiveDescendant: false);
        if (secondCycle.Kind != DysonTaskLifecycleKind.ReadyToFinalize)
        {
            throw new InvalidOperationException(
                "ReportSummary after historical BugReview should ReadyToFinalize, not a second review.");
        }
    }

    private static void AssertChildReflectionGate(DysonAgentTurnKind reflect)
    {
        var parent = new StubSession();
        var child = new StubSession();
        parent.RegisterForTest(child);

        var todo = child.CreateTodoAsync("open", "Finish verification", DysonSessionTodoStatus.Ongoing)
            .GetAwaiter()
            .GetResult();
        if (todo.IsError)
            throw new InvalidOperationException("Failed to create child todo: " + todo.Error);

        child.AddTurnForTest(Completed(DysonAgentSession.CreateNormalTurn("work remains")));
        if (!DysonTaskEndReflectFlow.TryCreateForChild(child, out var reflection)
            || reflection is null
            || reflection.Kind != reflect
            || !reflection.Instruction!.Contains("Finish verification", StringComparison.Ordinal)
            || !reflection.Instruction.Contains("Ongoing", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Incomplete child work must receive a TaskEndReflect turn with a compact todo snapshot.");
        }

        child.AddTurnForTest(Completed(reflection));
        if (DysonTaskEndReflectFlow.TryCreateForChild(child, out _))
        {
            throw new InvalidOperationException(
                "TaskEndReflect must not recursively queue another child reflection.");
        }
    }

    private static void AssertAutomaticReviewCompletionSuppression()
    {
        var parent = new StubSession();
        var reviewer = new StubSession(DysonAgentModes.BugReview);
        parent.RegisterForTest(reviewer);
        var completed = new DysonAgentInterrupt
        {
            Kind = DysonAgentInterruptKind.SubagentCompleted,
            SubagentId = reviewer.Id,
            Summary = "No bugs found.",
        };

        using (parent.BeginInFlightPrompt(new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.BugReview,
            StartedUtc = DateTime.UtcNow,
        }))
        {
            if (!Harness.UI.Demo.DysonSubagentHostLogic
                    .ShouldSuppressAutomaticReviewCompletion(parent, completed))
            {
                throw new InvalidOperationException(
                    "A BugReview turn must consume its waited Bug Review child without a duplicate generic report turn.");
            }
        }

        if (Harness.UI.Demo.DysonSubagentHostLogic
            .ShouldSuppressAutomaticReviewCompletion(parent, completed))
        {
            throw new InvalidOperationException(
                "Completed Bug Review children are suppressed only while the BugReview orchestration turn is in flight.");
        }
    }

    private static StubSession CreateRootWithCompletedTodoAndReport()
    {
        var session = new StubSession();
        var created = session.CreateTodoAsync("t1", "One", DysonSessionTodoStatus.Complete)
            .GetAwaiter()
            .GetResult();
        if (created.IsError)
            throw new InvalidOperationException("Failed to seed complete todo: " + created.Error);

        session.AddTurnForTest(Completed(DysonTaskCompletionFlow.CreateReportSummaryTurn("done")));
        return session;
    }

    private static DysonAgentTurn Completed(DysonAgentTurn turn)
    {
        turn.CompletedUtc = DateTime.UtcNow;
        return turn;
    }

    private static void ExpectNoLifecycle(DysonTaskLifecycleDecision decision, string because)
    {
        if (decision.HasAction || decision.Kind is not null)
            throw new InvalidOperationException($"Expected no lifecycle action ({because}); got {decision.Kind}.");
    }

    private static void AssertBugReviewSharedContract(string instruction, string level)
    {
        AssertContainsAll(
            instruction,
            $"{level} BugReview prompt",
            ["Bug Review", "agentMode", "WaitForSubagent", "modelSlug", "fix", "report"]);

        if (instruction.Contains("modelSlug:", StringComparison.Ordinal)
            || instruction.Contains("\"modelSlug\"", StringComparison.Ordinal)
            || ContainsAssignedModelSlug(instruction))
        {
            throw new InvalidOperationException(
                $"{level} BugReview prompt must omit StartSubagent.modelSlug (use Bug Review default / inherit).");
        }
    }

    private static bool ContainsAssignedModelSlug(string instruction)
    {
        foreach (var line in instruction.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("modelSlug", StringComparison.Ordinal)
                && (trimmed.Contains('=') || trimmed.Contains(':')))
            {
                if (trimmed.Contains("omit", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("without", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("do not", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("don't", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static void AssertNormalize(Type flowType)
    {
        var normalize = FindNormalize(flowType);
        if (normalize is null)
            throw new InvalidOperationException("DysonTaskLifecycleFlow must expose a level normalizer.");

        AssertNormalized(normalize, "none", "None");
        AssertNormalized(normalize, "LOW", "Low");
        AssertNormalized(normalize, "Medium", "Medium");
        AssertNormalized(normalize, "high", "High");
        AssertNormalized(normalize, " true ", expectedAnyOf: ["None", "High", "Medium"]);
    }

    private static void AssertHighDoesNotStartReview(Type flowType)
    {
        foreach (var name in new[] { "ShouldStartReview", "IsReviewEnabled", "CanStartReview" })
        {
            var method = flowType.GetMethod(
                name,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
            if (method is null || method.GetParameters().Length != 1)
                continue;

            var high = CoerceLevelArgument(method.GetParameters()[0].ParameterType, "high");
            var result = method.Invoke(null, [high]);
            if (result is bool start && start)
            {
                throw new InvalidOperationException(
                    $"{name}(High) must be false — high is unsupported and must not start a review.");
            }

            return;
        }
    }

    private static void AssertNormalized(
        MethodInfo normalize,
        string input,
        string? expected = null,
        string[]? expectedAnyOf = null)
    {
        var args = normalize.GetParameters();
        object? raw = args.Length switch
        {
            1 => CoerceLevelArgument(args[0].ParameterType, input),
            2 => null,
            _ => input,
        };

        object? result;
        if (args.Length == 1)
        {
            result = normalize.Invoke(null, [raw ?? input]);
        }
        else if (args.Length == 2
                 && args[0].ParameterType == typeof(string)
                 && args[1].ParameterType == typeof(string))
        {
            // Legacy pair: (endOfTaskAutoReview, selfReviewIntensity)
            result = normalize.Invoke(null, [input, input]);
        }
        else
        {
            result = normalize.Invoke(null, [input]);
        }

        var name = result?.ToString() ?? "";
        if (expected is not null
            && !name.Equals(expected, StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith("." + expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Normalize('{input}') expected {expected}, got '{name}'.");
        }

        if (expectedAnyOf is not null
            && !expectedAnyOf.Any(e =>
                name.Equals(e, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("." + e, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Normalize('{input}') expected one of {string.Join('/', expectedAnyOf)}, got '{name}'.");
        }
    }

    private static MethodInfo? FindNormalize(Type flowType)
    {
        foreach (var name in new[]
                 {
                     "Normalize",
                     "NormalizeLevel",
                     "ParseLevel",
                     "ParseReviewLevel",
                     "NormalizeReviewLevel",
                 })
        {
            var method = flowType.GetMethod(
                name,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
            if (method is not null)
                return method;
        }

        return flowType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m =>
                m.Name.Contains("Normalize", StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains("Parse", StringComparison.OrdinalIgnoreCase));
    }

    private static DysonAgentTurn InvokeTurnFactory(Type flowType, string methodName)
    {
        var method = flowType.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
        if (method is null)
            throw new InvalidOperationException($"Missing {flowType.Name}.{methodName}().");

        if (method.Invoke(null, method.GetParameters().Select(_ => (object?)null).ToArray())
            is not DysonAgentTurn turn)
        {
            throw new InvalidOperationException($"{methodName} must return DysonAgentTurn.");
        }

        return turn;
    }

    private static DysonAgentTurn InvokeBugReviewFactory(Type flowType, string level)
    {
        var method = flowType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Name, "CreateBugReviewTurn", StringComparison.OrdinalIgnoreCase)
                && candidate.GetParameters().Length == 1);
        if (method is null)
            throw new InvalidOperationException("Missing single-argument DysonTaskLifecycleFlow.CreateBugReviewTurn.");

        var parameters = method.GetParameters();
        if (parameters.Length != 1)
        {
            throw new InvalidOperationException(
                "CreateBugReviewTurn must take a single review-level argument.");
        }

        var arg = CoerceLevelArgument(parameters[0].ParameterType, level);
        if (method.Invoke(null, [arg]) is not DysonAgentTurn turn)
            throw new InvalidOperationException("CreateBugReviewTurn must return DysonAgentTurn.");

        return turn;
    }

    private static object CoerceLevelArgument(Type parameterType, string level)
    {
        if (parameterType == typeof(string))
            return level;

        if (parameterType.IsEnum)
        {
            foreach (var name in Enum.GetNames(parameterType))
            {
                if (name.Equals(level, StringComparison.OrdinalIgnoreCase))
                    return Enum.Parse(parameterType, name);
            }

            throw new InvalidOperationException(
                $"Review-level enum {parameterType.Name} has no '{level}' member.");
        }

        throw new InvalidOperationException(
            $"Cannot coerce review level '{level}' to {parameterType.FullName}.");
    }

    private static void AssertContainsAll(string text, string subject, string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException($"{subject} must mention '{needle}'.");
        }
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode = DysonAgentModes.Work) : DysonAgentSession(
        mode,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void ConfigureRootForTest() => ConfigureRootInterAgentTools();

        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

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
