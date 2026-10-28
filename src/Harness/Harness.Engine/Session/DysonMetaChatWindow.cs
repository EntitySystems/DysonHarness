namespace DysonHarness;

/// <summary>
/// Root Meta Agent chat window: the newest <see cref="MaxInteractions"/> chat bubbles, and which
/// older persisted turns may be hard-deleted on load. One interaction is one bubble the Meta chat
/// projection emits: a Normal turn with an instruction, images, or hidden attachments; each
/// non-blank injected user comment; a DisplayInfo turn with assistant text.
/// </summary>
public static class DysonMetaChatWindow
{
    public const int MaxInteractions = 100;

    /// <summary>True for a root (parentless) Meta Agent session. Only those are capped and trimmed.</summary>
    public static bool Applies(Guid? parentSessionId, string? agentMode) =>
        parentSessionId is null && DysonSessionPolicy.IsMetaAgent(agentMode);

    /// <summary>Number of Meta chat bubbles <paramref name="turn"/> projects to.</summary>
    public static int InteractionCount(DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        var count = 0;
        if (turn.Kind == DysonAgentTurnKind.Normal
            && (!string.IsNullOrWhiteSpace(turn.Instruction)
                || !string.IsNullOrWhiteSpace(turn.HiddenInstruction)
                || turn.UserImages.Any(i =>
                    !string.IsNullOrWhiteSpace(i.RemoteUrl) || !string.IsNullOrWhiteSpace(i.Base64Data))))
        {
            count++;
        }

        foreach (var segment in turn.ReasoningLog)
        {
            if (segment.Kind == DysonReasoningSegmentKind.UserComment && !string.IsNullOrWhiteSpace(segment.Text))
                count++;
        }

        if (turn.Kind == DysonAgentTurnKind.DisplayInfo && !string.IsNullOrWhiteSpace(turn.AssistantText))
            count++;

        return count;
    }

    /// <summary>
    /// Index of the first turn whose bubbles fall inside the newest <paramref name="max"/> interactions.
    /// Walks backward, so cost follows the window, not the history.
    /// </summary>
    public static int DisplayStart(IReadOnlyList<DysonAgentTurn> turns, int max = MaxInteractions)
    {
        ArgumentNullException.ThrowIfNull(turns);

        var count = 0;
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            count += InteractionCount(turns[i]);
            if (count >= max)
                return i;
        }

        return 0;
    }

    /// <summary>
    /// Turn ids before the display window that are safe to hard-delete. Empty when nothing qualifies.
    /// </summary>
    public static IReadOnlyList<Guid> SelectTrimmable(IReadOnlyList<DysonAgentTurn> turns, int max = MaxInteractions)
    {
        ArgumentNullException.ThrowIfNull(turns);

        // ponytail: the model-context ceiling is the latest successful compaction summary. It and every
        // later turn stay. Excluded turns before it only replay their user comments / summary stubs into the
        // transcript, and the summary supersedes them, so they may go. No summary yet means nothing is trimmed,
        // so an uncompacted session keeps growing in the DB (display is still capped). Upgrade path: trim on
        // save once compaction runs per window.
        var boundary = -1;
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            var t = turns[i];
            if (t.Kind == DysonAgentTurnKind.FullSummarize && !t.IsExcludedFromContext && t.CompletedUtc is not null)
            {
                boundary = i;
                break;
            }
        }

        var end = Math.Min(boundary, DisplayStart(turns, max));
        if (end <= 0)
            return [];

        // Keep render turns whose visualization a shown DisplayInfo still opens.
        var shownVisualizations = new HashSet<Guid>();
        for (var i = end; i < turns.Count; i++)
        {
            if (turns[i].VisualizationId is { } id)
                shownVisualizations.Add(id);
        }

        var ids = new List<Guid>();
        for (var i = 0; i < end; i++)
        {
            var t = turns[i];
            if (!t.IsExcludedFromContext || t.CompletedUtc is null)
                continue;
            if (t.UserQuestion is { Answer: null })
                continue;
            if (shownVisualizations.Count > 0
                && t.TrackedToolCalls.Any(c => c.Result?.HtmlVisualization?.Id is { } v && shownVisualizations.Contains(v)))
            {
                continue;
            }

            ids.Add(t.Id);
        }

        return ids;
    }
}
