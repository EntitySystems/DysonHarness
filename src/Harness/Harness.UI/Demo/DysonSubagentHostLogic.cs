using DysonHarness;

namespace Harness.UI.Demo;

/// <summary>Card + auto-turn helpers for <see cref="DysonUiHost"/> (pure logic; covered by Harness.Tests).</summary>
public static class DysonSubagentHostLogic
{
    // latestTurn kept for call-site compatibility; parent spinner follows session Status only.
    public static bool IsRunning(DysonSessionStatus status, DysonAgentTurn? latestTurn = null) =>
        status == DysonSessionStatus.Active;

    /// <summary>True when any descendant (any depth) has <see cref="DysonSessionStatus.Active"/>.</summary>
    public static bool HasActiveDescendant(DysonAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        foreach (var child in session.SubSessions)
        {
            if (IsRunning(child.Status) || HasActiveDescendant(child))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Skip SubagentReportProcessing when this completion was already delivered via
    /// <c>WaitForSubagent</c> (waiting or consume marker) or the in-flight BugReview helper.
    /// </summary>
    public static bool ShouldSuppressCompletionAutoTurn(
        DysonAgentSession parent,
        DysonAgentInterrupt interrupt)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(interrupt);

        if (!DysonSubagentReportPrompt.IsCompletionInterrupt(interrupt.Kind))
            return false;

        return parent.ShouldSuppressWaitedCompletionAutoTurn(interrupt.SubagentId)
            || ShouldSuppressAutomaticReviewCompletion(parent, interrupt);
    }

    /// <summary>
    /// The BugReview orchestration turn consumes its single review child's terminal result
    /// through WaitForSubagent. Keep as a dedicated OR term of
    /// <see cref="ShouldSuppressCompletionAutoTurn"/>.
    /// </summary>
    public static bool ShouldSuppressAutomaticReviewCompletion(
        DysonAgentSession parent,
        DysonAgentInterrupt interrupt)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(interrupt);

        if (!DysonSubagentReportPrompt.IsCompletionInterrupt(interrupt.Kind)
            || parent.InFlightPromptTurn?.Kind != DysonAgentTurnKind.BugReview
            || !parent.TryGetSubagent(interrupt.SubagentId, out var child))
        {
            return false;
        }

        return string.Equals(child.Mode, DysonAgentModes.BugReview, StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildSubagentReportContinuationPrompt(DysonAgentInterrupt interrupt, string? title) =>
        DysonSubagentReportPrompt.BuildContinuationPrompt(interrupt, title);

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

    /// <summary>
    /// True only when kind is promptUserDialog and payload parses as PromptUserDialog JSON (modal path).
    /// </summary>
    public static bool TryBuildUserDialogUi(
        string? eventKind,
        string? payload,
        out DysonPromptUserDialogRequest dialog)
    {
        dialog = null!;
        if (!string.Equals(
                eventKind,
                DysonPromptUserDialog.PromptUserDialogKind,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parsed = DysonPromptUserDialog.ParseDialogJson(payload);
        if (parsed.IsError)
            return false;

        dialog = parsed.Value;
        return true;
    }

    /// <summary>Parent must enqueue DrainAutoTurnsAsync whenever Ask / Dialog UI is not opened for this event.</summary>
    public static bool RequiresParentAutoTurn(string? eventKind, string? payload) =>
        !TryBuildAskUi(eventKind, payload, out _) && !TryBuildUserDialogUi(eventKind, payload, out _);

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
}

/// <summary>Live snapshot for parent <c>SubagentCard</c> UI.</summary>
public sealed class DysonSubagentCardState
{
    public required Guid PersistenceId { get; init; }
    /// <summary>Child session runtime id (<see cref="DysonAgentSession.Id"/>).</summary>
    public int RuntimeId { get; init; }
    public string? Title { get; init; }
    /// <summary>
    /// Latest visible thinking/step label for the child's current turn (Engine
    /// <see cref="DysonReasoningHistoryUi.TryGetLatestStepTitle"/>). Presentation-only.
    /// </summary>
    public string? LatestTurnStepTitle { get; init; }
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

public enum DysonUserDialogUiSource
{
    RootPromptUserDialog = 0,
    ParentEventPromptUserDialog = 1,
}

public sealed class DysonUserDialogUiState
{
    public required DysonUserDialogUiSource Source { get; init; }
    public required Guid SessionPersistenceId { get; init; }
    public Guid? EventId { get; init; }
    public int? SubagentId { get; init; }
    public required DysonPromptUserDialogRequest Dialog { get; init; }
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
