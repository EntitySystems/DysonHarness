using DysonHarness;

namespace Harness.UI.Demo;

/// <summary>Card + auto-turn helpers for <see cref="DysonUiHost"/> (pure; self-checkable).</summary>
public static class DysonSubagentHostLogic
{
    // latestTurn kept for call-site compatibility; parent spinner follows session Status only.
    public static bool IsRunning(DysonSessionStatus status, DysonAgentTurn? latestTurn = null) =>
        status == DysonSessionStatus.Active;

    public static string BuildSubagentReportContinuationPrompt(DysonAgentInterrupt interrupt, string? title)
    {
        ArgumentNullException.ThrowIfNull(interrupt);

        var outcome = interrupt.Kind switch
        {
            DysonAgentInterruptKind.SubagentCompleted => "completed",
            DysonAgentInterruptKind.SubagentFailed => "failed",
            DysonAgentInterruptKind.SubagentStopped => "stopped",
            _ => interrupt.Kind.ToString(),
        };

        var titleLine = string.IsNullOrWhiteSpace(title) ? "(untitled)" : title.Trim();
        var summary = string.IsNullOrWhiteSpace(interrupt.Summary)
            ? "(no summary)"
            : interrupt.Summary.Trim();

        var persistence = interrupt.PersistenceId is Guid pid && pid != Guid.Empty
            ? pid.ToString("D")
            : "(unknown)";

        return
            $"""
            Harness continuation: a subagent finished and submitted a report. Incorporate it and continue the parent task.

            - subagentId: {interrupt.SubagentId}
            - persistenceId: {persistence}
            - title: {titleLine}
            - outcome: {outcome}

            ## Report
            {summary}
            """;
    }

    public static string BuildSubagentEventContinuationPrompt(DysonAgentInterrupt interrupt, string? title)
    {
        ArgumentNullException.ThrowIfNull(interrupt);

        var titleLine = string.IsNullOrWhiteSpace(title) ? "(untitled)" : title.Trim();
        var persistence = interrupt.PersistenceId is Guid pid && pid != Guid.Empty
            ? pid.ToString("D")
            : "(unknown)";
        var eventId = interrupt.EventId is Guid eid && eid != Guid.Empty
            ? eid.ToString("D")
            : "(unknown)";
        var kind = string.IsNullOrWhiteSpace(interrupt.EventKind) ? "(unknown)" : interrupt.EventKind.Trim();
        var payload = string.IsNullOrWhiteSpace(interrupt.Payload) ? "(empty)" : interrupt.Payload.Trim();

        return
            $"""
            Harness continuation: a subagent triggered a parent event. Address it with RespondToSubagentEvent, then continue.

            - subagentId: {interrupt.SubagentId}
            - persistenceId: {persistence}
            - title: {titleLine}
            - eventId: {eventId}
            - kind: {kind}

            ## Payload
            {payload}

            Call RespondToSubagentEvent with subagentId, eventId, and your reply string so the child can unblock.
            """;
    }

    /// <summary>
    /// True only when kind is askQuestion and payload parses as AskQuestion questions JSON (Ask UI path).
    /// Plain-text askQuestion and all other kinds return false (parent auto-turn required).
    /// </summary>
    public static bool TryBuildAskUi(
        string? eventKind,
        string? payload,
        out IReadOnlyList<DysonAskQuestionItem> questions)
    {
        questions = [];
        if (!string.Equals(eventKind, DysonAskQuestion.AskQuestionKind, StringComparison.OrdinalIgnoreCase))
            return false;

        var parsed = DysonAskQuestion.ParseQuestionsJson(payload);
        if (parsed.IsError)
            return false;

        questions = parsed.Value;
        return true;
    }

    /// <summary>Parent must enqueue DrainAutoTurnsAsync whenever Ask UI is not opened for this event.</summary>
    public static bool RequiresParentAutoTurn(string? eventKind, string? payload) =>
        !TryBuildAskUi(eventKind, payload, out _);

    /// <summary>ponytail: assert-based check for IsRunning + prompt shape; no test framework.</summary>
    public static void RunSelfCheck()
    {
        var activeNoTurns = IsRunning(DysonSessionStatus.Active, latestTurn: null);
        if (!activeNoTurns)
            throw new InvalidOperationException("Active with no turns should be running.");

        var inFlight = IsRunning(
            DysonSessionStatus.Active,
            new DysonAgentTurn { StartedUtc = DateTime.UtcNow, CompletedUtc = null });
        if (!inFlight)
            throw new InvalidOperationException("Active turn without CompletedUtc should be running.");

        var doneTurn = IsRunning(
            DysonSessionStatus.Active,
            new DysonAgentTurn { StartedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow });
        if (!doneTurn)
            throw new InvalidOperationException("Active with completed latest turn should still be running.");

        if (IsRunning(DysonSessionStatus.Completed, latestTurn: null))
            throw new InvalidOperationException("Completed status should not be running.");

        if (IsRunning(DysonSessionStatus.Failed, latestTurn: null))
            throw new InvalidOperationException("Failed status should not be running.");

        if (IsRunning(DysonSessionStatus.Stopped, latestTurn: null))
            throw new InvalidOperationException("Stopped status should not be running.");

        var prompt = BuildSubagentReportContinuationPrompt(
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
            || !prompt.Contains("Explore README", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Continuation prompt missing expected fields.");
        }

        var eventPrompt = BuildSubagentEventContinuationPrompt(
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
        AssertKickOffFailureSummaries();
        AssertPromptQueueFifo();
    }

    /// <summary>
    /// Plain-text askQuestion and non-ask kinds → auto-turn; valid questions JSON askQuestion → Ask UI only.
    /// </summary>
    private static void AssertAskUiRouting()
    {
        const string validQuestions =
            """{"questions":[{"prompt":"Name?","options":["A","B"]}]}""";

        if (!TryBuildAskUi(DysonAskQuestion.AskQuestionKind, validQuestions, out var qs)
            || qs.Count != 1
            || RequiresParentAutoTurn(DysonAskQuestion.AskQuestionKind, validQuestions))
        {
            throw new InvalidOperationException(
                "Valid askQuestion questions JSON should open Ask UI and skip auto-turn.");
        }

        const string plainText = "What should the sleepy robot's name be?";
        if (TryBuildAskUi(DysonAskQuestion.AskQuestionKind, plainText, out _)
            || !RequiresParentAutoTurn(DysonAskQuestion.AskQuestionKind, plainText))
        {
            throw new InvalidOperationException(
                "Plain-text askQuestion must require parent auto-turn (no Ask UI).");
        }

        if (TryBuildAskUi("message", "hello", out _)
            || !RequiresParentAutoTurn("message", "hello"))
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
        var continuation = BuildSubagentEventContinuationPrompt(plainAskInterrupt, title: "Child");
        if (!continuation.Contains("eventId: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", StringComparison.Ordinal)
            || !continuation.Contains("RespondToSubagentEvent", StringComparison.Ordinal)
            || !continuation.Contains(plainText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Plain-text askQuestion auto-turn prompt must include eventId + RespondToSubagentEvent.");
        }
    }

    /// <summary>First non-empty line of a prompt (queue popover preview).</summary>
    public static string PromptFirstLine(string prompt)
    {
        var trimmed = prompt.AsSpan().Trim();
        var idx = trimmed.IndexOfAny('\r', '\n');
        return idx < 0 ? trimmed.ToString() : trimmed[..idx].TrimEnd().ToString();
    }

    /// <summary>Formats provider label like SessionHeader: <c>Alias · Provider / slug</c>.</summary>
    public static string? FormatProviderModelLabel(DysonAgentProvider? provider) =>
        provider switch
        {
            DemoDysonAgentProvider demo =>
                $"{demo.DisplayAlias} · {demo.ProviderDisplayName} / {demo.Slug}",
            OpenAiCompatibleAgentProvider oai =>
                $"{oai.DisplayAlias} · {oai.ProviderDisplayName} / {oai.Slug}",
            _ => null,
        };

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

    /// <summary>ponytail: FIFO enqueue + remove-by-id + first-line preview (mirrors host queue).</summary>
    private static void AssertPromptQueueFifo()
    {
        if (!string.Equals(PromptFirstLine("  hello\nworld  "), "hello", StringComparison.Ordinal))
            throw new InvalidOperationException("PromptFirstLine should return first trimmed line.");

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

/// <summary>Live snapshot for parent <c>SubagentCard</c> UI.</summary>
public sealed class DysonSubagentCardState
{
    public required Guid PersistenceId { get; init; }
    public string? Title { get; init; }
    public string? LatestTurnAgentTitle { get; init; }
    public string? ModelLabel { get; init; }
    /// <summary>Child session agent mode (<see cref="DysonAgentSession.Mode"/>).</summary>
    public string? AgentMode { get; init; }
    public bool IsRunning { get; init; }
    public DysonSessionStatus Status { get; init; }
}

public enum DysonAskUiSource
{
    RootAskQuestion = 0,
    ParentEventAskQuestion = 1,
}

public sealed class DysonAskUiState
{
    public required DysonAskUiSource Source { get; init; }
    public required Guid SessionPersistenceId { get; init; }
    public Guid? EventId { get; init; }
    public int? SubagentId { get; init; }
    public required IReadOnlyList<DysonAskQuestionItem> Questions { get; init; }
}

public sealed class DysonSubagentEventUiItem
{
    public required Guid EventId { get; init; }
    public Guid ParentPersistenceId { get; init; }
    public required int SubagentId { get; init; }
    public string? SubagentTitle { get; set; }
    public required string Kind { get; set; }
    public required string Payload { get; set; }
    public bool IsAddressed { get; set; }
    public DateTimeOffset Timestamp { get; init; }
}
