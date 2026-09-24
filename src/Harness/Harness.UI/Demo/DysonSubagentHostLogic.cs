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

    public static string BuildSubagentEventContinuationPrompt(
        DysonAgentInterrupt interrupt,
        string? title,
        string? parentMode = null)
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
            - subagentId: {interrupt.SubagentId}
            - persistenceId: {persistence}
            - title: {titleLine}
            - eventId: {eventId}
            - kind: {kind}

            ## Payload
            {payload}

            {ParentEventReplyContract(parentMode)}
            """;
    }

    /// <summary>Lead line plus the Meta Agent continuation. Same text as the auto-turn and the one retry.</summary>
    public static string BuildParkedParentEventReminder(DysonAgentInterrupt interrupt, string? title) =>
        "Still pending. The user message is their answer. Call RespondToSubagentEvent with that answer for this same eventId. Do not ask again.\n"
        + BuildSubagentEventContinuationPrompt(interrupt, title, DysonAgentModes.MetaAgent);

    private static string ParentEventReplyContract(string? parentMode)
    {
        if (string.Equals(parentMode, DysonAgentModes.MetaAgent, StringComparison.OrdinalIgnoreCase))
        {
            return
                """
                These instructions are the reply contract for this event. Do not rely on an earlier system prompt.

                Call RespondToSubagentEvent before this turn ends, unless only the user can decide.
                Status (what landed, what is next): ack on this same turn and PostConversationMessage the status.
                A question you already know: answer on this same turn.
                A question only the user can decide: PostConversationMessage the question, do not respond yet, keep this subagentId and eventId, and RespondToSubagentEvent when the user answers. Do not start another drone for the same question.
                Example: RespondToSubagentEvent(subagentId, eventId, reply)
                """;
        }

        if (string.Equals(parentMode, DysonAgentModes.MetaAgentDrone, StringComparison.OrdinalIgnoreCase))
        {
            return
                """
                These instructions are the reply contract for this event. Do not rely on an earlier system prompt.

                You are the parent of this event. Call RespondToSubagentEvent before this turn ends.
                Status: short ack on this same turn.
                A question you know: answer on this same turn.
                A question you do not know: TriggerParentEvent to your own parent with kind message, wait for that reply, then RespondToSubagentEvent to this child. You cannot PostConversationMessage. Do not use kind askQuestion or promptUserDialog.
                Example: RespondToSubagentEvent(subagentId, eventId, reply)
                """;
        }

        return
            """
            These instructions are the reply contract for this event.

            Call RespondToSubagentEvent before this turn ends. Ack a status. Answer a question.
            Example: RespondToSubagentEvent(subagentId, eventId, reply)
            """;
    }

    /// <summary>
    /// Wraps <see cref="BuildSubagentEventContinuationPrompt"/> as a
    /// <see cref="DysonAgentTurnKind.ParentEvent"/> turn (not a user message).
    /// </summary>
    public static DysonAgentTurn CreateTurn(string prompt) =>
        new()
        {
            Kind = DysonAgentTurnKind.ParentEvent,
            Instruction = prompt,
            StartedUtc = DateTime.UtcNow,
        };

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

    /// <summary>
    /// Meta parents always auto-turn. Other modes skip the turn when Ask / Dialog UI can open.
    /// </summary>
    public static bool RequiresParentAutoTurn(string? eventKind, string? payload, string? parentMode = null)
    {
        if (IsMetaSessionMode(parentMode))
            return true;

        return !TryBuildAskUi(eventKind, payload, out _)
            && !TryBuildUserDialogUi(eventKind, payload, out _);
    }

    public static bool IsMetaSessionMode(string? mode) =>
        string.Equals(mode, DysonAgentModes.MetaAgent, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, DysonAgentModes.MetaAgentDrone, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ponytail: one re-inject after a forgotten meta auto-turn. The next forgotten turn fails the child.
    /// Upgrade path: raise <see cref="MetaParentEventRetryCap"/> if one nudge is not enough.
    /// </summary>
    public const int MetaParentEventRetryCap = 1;

    public static MetaParentEventAfterTurn DecideMetaParentEventAfterTurn(
        bool metaParent,
        bool stillPending,
        bool rootMeta,
        bool postedDisplayInfoThisTurn,
        int forgottenAutoTurns)
    {
        if (!metaParent || !stillPending)
            return MetaParentEventAfterTurn.Done;
        if (rootMeta && postedDisplayInfoThisTurn)
            return MetaParentEventAfterTurn.Park;
        if (forgottenAutoTurns >= MetaParentEventRetryCap)
            return MetaParentEventAfterTurn.Fail;
        return MetaParentEventAfterTurn.Retry;
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

public enum MetaParentEventAfterTurn
{
    Done = 0,
    Retry = 1,
    Park = 2,
    Fail = 3,
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
