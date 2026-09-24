using DysonHarness;
using Harness.UI.Demo;

namespace Harness.Tests;

/// <summary>Subagent host IsRunning + prompt shape + Ask UI routing (Xunit).</summary>
public class DysonSubagentHostLogicTests
{
    [Fact]
    public void Run()
    {
        var activeNoTurns = DysonSubagentHostLogic.IsRunning(DysonSessionStatus.Active, latestTurn: null);
        if (!activeNoTurns)
            throw new InvalidOperationException("Active with no turns should be running.");

        var inFlight = DysonSubagentHostLogic.IsRunning(
            DysonSessionStatus.Active,
            new DysonAgentTurn { StartedUtc = DateTime.UtcNow, CompletedUtc = null });
        if (!inFlight)
            throw new InvalidOperationException("Active turn without CompletedUtc should be running.");

        var doneTurn = DysonSubagentHostLogic.IsRunning(
            DysonSessionStatus.Active,
            new DysonAgentTurn { StartedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow });
        if (!doneTurn)
            throw new InvalidOperationException("Active with completed latest turn should still be running.");

        if (DysonSubagentHostLogic.IsRunning(DysonSessionStatus.Completed, latestTurn: null))
            throw new InvalidOperationException("Completed status should not be running.");

        if (DysonSubagentHostLogic.IsRunning(DysonSessionStatus.Failed, latestTurn: null))
            throw new InvalidOperationException("Failed status should not be running.");

        if (DysonSubagentHostLogic.IsRunning(DysonSessionStatus.Stopped, latestTurn: null))
            throw new InvalidOperationException("Stopped status should not be running.");

        if (DysonSubagentHostLogic.IsRunning(DysonSessionStatus.Interrupted, latestTurn: null))
            throw new InvalidOperationException("Interrupted status should not be running.");

        AssertHasActiveDescendant();

        var prompt = DysonSubagentHostLogic.BuildSubagentReportContinuationPrompt(
            new DysonAgentInterrupt
            {
                Kind = DysonAgentInterruptKind.SubagentCompleted,
                SubagentId = 2,
                PersistenceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                Summary = "Found 3 files.",
            },
            title: "Explore README");

        if (!prompt.Contains("subagentId: 2", StringComparison.Ordinal)
            || !prompt.Contains("outcome: completed", StringComparison.Ordinal)
            || !prompt.Contains("Found 3 files.", StringComparison.Ordinal)
            || !prompt.Contains("Explore README", StringComparison.Ordinal)
            || !prompt.Contains("# Subagent report", StringComparison.Ordinal)
            || !prompt.Contains("concrete technical continuation", StringComparison.OrdinalIgnoreCase)
            || !prompt.Contains("Do not wait for another harness turn", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Continuation prompt missing expected fields.");
        }

        var eventPrompt = DysonSubagentHostLogic.BuildSubagentEventContinuationPrompt(
            new DysonAgentInterrupt
            {
                Kind = DysonAgentInterruptKind.SubagentEvent,
                SubagentId = 3,
                EventId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                EventKind = "status",
                Payload = "{\"ok\":true}",
            },
            title: "Drone A");

        if (!eventPrompt.Contains("eventId: 11111111-2222-3333-4444-555555555555", StringComparison.Ordinal)
            || !eventPrompt.Contains("subagentId: 3", StringComparison.Ordinal)
            || !eventPrompt.Contains("Ack a status", StringComparison.Ordinal)
            || !eventPrompt.Contains("Answer a question", StringComparison.Ordinal)
            || !eventPrompt.Contains("RespondToSubagentEvent(subagentId, eventId, reply)", StringComparison.Ordinal)
            || !eventPrompt.Contains("{\"ok\":true}", StringComparison.Ordinal)
            || eventPrompt.Contains("PostConversationMessage", StringComparison.Ordinal)
            || eventPrompt.Contains("TriggerParentEvent", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Event continuation prompt missing expected fields.");
        }

        AssertParentEventReplyContracts();

        var parentEventTurn = DysonSubagentHostLogic.CreateTurn(eventPrompt);
        if (parentEventTurn.Kind != DysonAgentTurnKind.ParentEvent
            || parentEventTurn.Instruction is not { } instruction
            || !instruction.Contains("eventId:", StringComparison.Ordinal)
            || !instruction.Contains("RespondToSubagentEvent", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "CreateTurn must be ParentEvent and keep eventId: and RespondToSubagentEvent.");
        }

        AssertAskUiRouting();
        AssertUserDialogUiRouting();
        AssertKickOffFailureSummaries();
        AssertPromptQueueFifo();
        AssertCompletionAutoTurnSuppression();
    }

    private static void AssertCompletionAutoTurnSuppression()
    {
        string[] parentModes =
        [
            DysonAgentModes.Work,
            DysonAgentModes.Plan,
            DysonAgentModes.Ask,
            DysonAgentModes.Drone,
        ];
        DysonAgentInterruptKind[] completionKinds =
        [
            DysonAgentInterruptKind.SubagentCompleted,
            DysonAgentInterruptKind.SubagentFailed,
            DysonAgentInterruptKind.SubagentStopped,
        ];

        foreach (var mode in parentModes)
        {
            var parent = new StubSession(mode);
            var waited = new StubSession();
            var sibling = new StubSession();
            parent.RegisterForTest(waited);
            parent.RegisterForTest(sibling);

            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var waitTask = parent.WaitForSubagentAsync(waited.Id, timeoutMs: 15_000, waitCts.Token);
            if (!SpinUntil(() => parent.WaitingOnSubagentIds.Contains(waited.Id), TimeSpan.FromSeconds(2)))
            {
                waitCts.Cancel();
                throw new InvalidOperationException($"Wait id never appeared for parent mode {mode}.");
            }

            try
            {
                foreach (var kind in completionKinds)
                {
                    var matching = new DysonAgentInterrupt { Kind = kind, SubagentId = waited.Id };
                    if (!DysonSubagentHostLogic.ShouldSuppressCompletionAutoTurn(parent, matching))
                    {
                        throw new InvalidOperationException(
                            $"{mode} parent waiting on child must suppress {kind}.");
                    }

                    var other = new DysonAgentInterrupt { Kind = kind, SubagentId = sibling.Id };
                    if (DysonSubagentHostLogic.ShouldSuppressCompletionAutoTurn(parent, other))
                    {
                        throw new InvalidOperationException(
                            $"{mode} parent must not suppress a non-waited sibling {kind}.");
                    }
                }

                var eventInterrupt = new DysonAgentInterrupt
                {
                    Kind = DysonAgentInterruptKind.SubagentEvent,
                    SubagentId = waited.Id,
                    EventKind = "status",
                    Payload = "ok",
                };
                if (DysonSubagentHostLogic.ShouldSuppressCompletionAutoTurn(parent, eventInterrupt))
                {
                    throw new InvalidOperationException(
                        "SubagentEvent must not be suppressed even while waiting on that child.");
                }
            }
            finally
            {
                waitCts.Cancel();
                try
                {
                    waitTask.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        var consumeParent = new StubSession(DysonAgentModes.Work);
        var consumeChild = new StubSession();
        consumeParent.RegisterForTest(consumeChild);
        if (!consumeChild.TryMarkTerminal(DysonSessionStatus.Completed, "waited"))
            throw new InvalidOperationException("Expected TryMarkTerminal Completed for consume child.");

        var consumeWait = consumeParent.WaitForSubagentAsync(consumeChild.Id, timeoutMs: 2000)
            .GetAwaiter()
            .GetResult();
        if (consumeWait.IsError)
            throw new InvalidOperationException("Expected successful Wait: " + consumeWait.Error);

        if (!consumeParent.HasWaitConsumedCompletion(consumeChild.Id))
            throw new InvalidOperationException("Expected HasWaitConsumedCompletion after successful Wait.");

        var consumed = new DysonAgentInterrupt
        {
            Kind = DysonAgentInterruptKind.SubagentCompleted,
            SubagentId = consumeChild.Id,
        };
        if (!DysonSubagentHostLogic.ShouldSuppressCompletionAutoTurn(consumeParent, consumed))
        {
            throw new InvalidOperationException(
                "Consume marker must still suppress completion auto-turn after Wait returns.");
        }
    }

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(10);
        }

        return condition();
    }

    private static void AssertAskUiRouting()
    {
        const string validQuestions =
            """{"questions":[{"prompt":"Name?","options":["A","B"]}]}""";

        if (!DysonSubagentHostLogic.TryBuildAskUi(DysonAskQuestion.AskQuestionKind, validQuestions, out var qs)
            || qs.Count != 1
            || DysonSubagentHostLogic.RequiresParentAutoTurn(DysonAskQuestion.AskQuestionKind, validQuestions)
            || DysonSubagentHostLogic.RequiresParentAutoTurn(
                DysonAskQuestion.AskQuestionKind, validQuestions, DysonAgentModes.Work))
        {
            throw new InvalidOperationException(
                "Valid askQuestion questions JSON should open Ask UI and skip auto-turn.");
        }

        if (!DysonSubagentHostLogic.RequiresParentAutoTurn(
                DysonAskQuestion.AskQuestionKind, validQuestions, DysonAgentModes.MetaAgent)
            || !DysonSubagentHostLogic.RequiresParentAutoTurn(
                DysonAskQuestion.AskQuestionKind, validQuestions, DysonAgentModes.MetaAgentDrone))
        {
            throw new InvalidOperationException(
                "Meta parents must auto-turn valid askQuestion JSON.");
        }

        const string plainText = "What should the sleepy robot's name be?";
        if (DysonSubagentHostLogic.TryBuildAskUi(DysonAskQuestion.AskQuestionKind, plainText, out _)
            || !DysonSubagentHostLogic.RequiresParentAutoTurn(DysonAskQuestion.AskQuestionKind, plainText))
        {
            throw new InvalidOperationException(
                "Plain-text askQuestion must require parent auto-turn (no Ask UI).");
        }

        if (DysonSubagentHostLogic.TryBuildAskUi("message", "hello", out _)
            || !DysonSubagentHostLogic.RequiresParentAutoTurn("message", "hello"))
        {
            throw new InvalidOperationException("message kind must require parent auto-turn.");
        }

        var plainAskInterrupt = new DysonAgentInterrupt
        {
            Kind = DysonAgentInterruptKind.SubagentEvent,
            SubagentId = 4,
            EventId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            EventKind = DysonAskQuestion.AskQuestionKind,
            Payload = plainText,
        };
        var continuation = DysonSubagentHostLogic.BuildSubagentEventContinuationPrompt(
            plainAskInterrupt, title: "Child");
        if (!continuation.Contains("eventId: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", StringComparison.Ordinal)
            || !continuation.Contains("RespondToSubagentEvent", StringComparison.Ordinal)
            || !continuation.Contains(plainText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Plain-text askQuestion auto-turn prompt must include eventId + RespondToSubagentEvent.");
        }
    }

    private static void AssertParentEventReplyContracts()
    {
        var interrupt = new DysonAgentInterrupt
        {
            Kind = DysonAgentInterruptKind.SubagentEvent,
            SubagentId = 3,
            EventId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            EventKind = "status",
            Payload = "{\"ok\":true}",
        };

        var meta = DysonSubagentHostLogic.BuildSubagentEventContinuationPrompt(
            interrupt, "Drone A", DysonAgentModes.MetaAgent);
        var metaAgain = DysonSubagentHostLogic.BuildSubagentEventContinuationPrompt(
            interrupt, "Drone A", DysonAgentModes.MetaAgent);
        if (!string.Equals(meta, metaAgain, StringComparison.Ordinal))
            throw new InvalidOperationException("Meta Agent continuation must be stable across calls.");

        if (!meta.Contains("PostConversationMessage the status", StringComparison.Ordinal)
            || !meta.Contains("only the user can decide", StringComparison.Ordinal)
            || !meta.Contains("Do not start another drone", StringComparison.Ordinal)
            || !meta.Contains("RespondToSubagentEvent(subagentId, eventId, reply)", StringComparison.Ordinal)
            || !meta.Contains("subagentId: 3", StringComparison.Ordinal)
            || !meta.Contains("eventId: 11111111-2222-3333-4444-555555555555", StringComparison.Ordinal)
            || meta.Contains("TriggerParentEvent", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Meta Agent continuation missing reply contract.");
        }

        var drone = DysonSubagentHostLogic.BuildSubagentEventContinuationPrompt(
            interrupt, "Drone A", DysonAgentModes.MetaAgentDrone);
        if (!drone.Contains("short ack", StringComparison.Ordinal)
            || !drone.Contains("TriggerParentEvent", StringComparison.Ordinal)
            || !drone.Contains("You cannot PostConversationMessage", StringComparison.Ordinal)
            || !drone.Contains("askQuestion", StringComparison.Ordinal)
            || !drone.Contains("RespondToSubagentEvent(subagentId, eventId, reply)", StringComparison.Ordinal)
            || !drone.Contains("subagentId: 3", StringComparison.Ordinal)
            || !drone.Contains("eventId: 11111111-2222-3333-4444-555555555555", StringComparison.Ordinal)
            || drone.Contains("PostConversationMessage the status", StringComparison.Ordinal)
            || drone.Contains("PostConversationMessage the question", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Meta Agent Drone continuation missing reply contract.");
        }

        var work = DysonSubagentHostLogic.BuildSubagentEventContinuationPrompt(
            interrupt, "Drone A", DysonAgentModes.Work);
        if (!work.Contains("Ack a status", StringComparison.Ordinal)
            || !work.Contains("Answer a question", StringComparison.Ordinal)
            || !work.Contains("RespondToSubagentEvent(subagentId, eventId, reply)", StringComparison.Ordinal)
            || !work.Contains("subagentId: 3", StringComparison.Ordinal)
            || !work.Contains("eventId: 11111111-2222-3333-4444-555555555555", StringComparison.Ordinal)
            || work.Contains("PostConversationMessage", StringComparison.Ordinal)
            || work.Contains("TriggerParentEvent", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Work continuation must stay generic.");
        }

        var reminder = DysonSubagentHostLogic.BuildParkedParentEventReminder(interrupt, "Drone A");
        if (!reminder.Contains(
                "Still pending. The user message is their answer. Call RespondToSubagentEvent with that answer for this same eventId. Do not ask again.",
                StringComparison.Ordinal)
            || !reminder.Contains(meta, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Parked reminder must embed the Meta Agent continuation.");
        }
    }

    private static void AssertUserDialogUiRouting()
    {
        const string validDialog =
            """
            {
              "title": "Next",
              "description": "Pick one",
              "actions": [{ "label": "Go", "primary": true }, { "label": "Wait" }]
            }
            """;

        if (!DysonSubagentHostLogic.TryBuildUserDialogUi(
                DysonPromptUserDialog.PromptUserDialogKind, validDialog, out var dialog)
            || dialog.Actions.Count != 2
            || DysonSubagentHostLogic.RequiresParentAutoTurn(
                DysonPromptUserDialog.PromptUserDialogKind, validDialog))
        {
            throw new InvalidOperationException(
                "Valid promptUserDialog JSON should open Dialog UI and skip auto-turn.");
        }

        const string plainText = "Should we continue?";
        if (DysonSubagentHostLogic.TryBuildUserDialogUi(
                DysonPromptUserDialog.PromptUserDialogKind, plainText, out _)
            || !DysonSubagentHostLogic.RequiresParentAutoTurn(
                DysonPromptUserDialog.PromptUserDialogKind, plainText))
        {
            throw new InvalidOperationException(
                "Plain-text promptUserDialog must require parent auto-turn (no Dialog UI).");
        }
    }

    private static void AssertKickOffFailureSummaries()
    {
        var exSummary = DysonAgentSession.FormatKickOffExceptionSummary(
            new InvalidOperationException("boom", new ArgumentException("inner")));
        if (!exSummary.Contains("InvalidOperationException: boom", StringComparison.Ordinal)
            || !exSummary.Contains("ArgumentException: inner", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Exception summary shape wrong: {exSummary}");
        }
    }

    private static void AssertPromptQueueFifo()
    {
        if (!string.Equals(
                DysonSubagentHostLogic.PromptFirstLine("  hello\nworld  "),
                "hello",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("PromptFirstLine should return first trimmed line.");
        }

        var list = new List<(Guid Id, string Text)>();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        list.Add((a, "one"));
        list.Add((b, "two"));
        list.Add((c, "three"));

        list.RemoveAll(e => e.Id == b);
        if (list.Count != 2 || list[0].Id != a || list[1].Id != c)
            throw new InvalidOperationException("Remove-by-id should preserve FIFO of remaining items.");

        var drained = list[0];
        list.RemoveAt(0);
        if (drained.Text != "one" || list[0].Text != "three")
            throw new InvalidOperationException("Drain should pop front in enqueue order.");
    }

    private static void AssertHasActiveDescendant()
    {
        var root = new StubSession();
        if (DysonSubagentHostLogic.HasActiveDescendant(root))
            throw new InvalidOperationException("Empty SubSessions should not report active descendants.");

        var child = new StubSession();
        var grandchild = new StubSession();
        root.RegisterForTest(child);
        child.RegisterForTest(grandchild);

        if (!DysonSubagentHostLogic.HasActiveDescendant(root))
            throw new InvalidOperationException("Active grandchild should make HasActiveDescendant true.");

        if (!grandchild.TryMarkTerminal(DysonSessionStatus.Stopped, "done"))
            throw new InvalidOperationException("Expected grandchild TryMarkTerminal to succeed.");
        if (!child.TryMarkTerminal(DysonSessionStatus.Completed, "done"))
            throw new InvalidOperationException("Expected child TryMarkTerminal to succeed.");

        if (DysonSubagentHostLogic.HasActiveDescendant(root))
            throw new InvalidOperationException("Terminal descendants should not report as active.");

        var interrupted = new StubSession();
        root.RegisterForTest(interrupted);
        if (!interrupted.TryMarkTerminal(DysonSessionStatus.Interrupted, "process restart"))
            throw new InvalidOperationException("Expected Interrupted TryMarkTerminal to succeed.");
        if (DysonSubagentHostLogic.HasActiveDescendant(root))
            throw new InvalidOperationException("Interrupted descendants should not report as active.");
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode = DysonAgentModes.Work) : DysonAgentSession(
        mode,
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
