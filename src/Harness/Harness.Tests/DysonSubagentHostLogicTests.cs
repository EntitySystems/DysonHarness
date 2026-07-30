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
            || !eventPrompt.Contains("RespondToSubagentEvent", StringComparison.Ordinal)
            || !eventPrompt.Contains("{\"ok\":true}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Event continuation prompt missing expected fields.");
        }

        AssertAskUiRouting();
        AssertUserDialogUiRouting();
        AssertKickOffFailureSummaries();
        AssertPromptQueueFifo();
    }

    private static void AssertAskUiRouting()
    {
        const string validQuestions =
            """{"questions":[{"prompt":"Name?","options":["A","B"]}]}""";

        if (!DysonSubagentHostLogic.TryBuildAskUi(DysonAskQuestion.AskQuestionKind, validQuestions, out var qs)
            || qs.Count != 1
            || DysonSubagentHostLogic.RequiresParentAutoTurn(DysonAskQuestion.AskQuestionKind, validQuestions))
        {
            throw new InvalidOperationException(
                "Valid askQuestion questions JSON should open Ask UI and skip auto-turn.");
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
}
