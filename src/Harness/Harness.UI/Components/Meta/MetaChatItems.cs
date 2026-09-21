using DysonHarness;
using Harness.UI.Demo;

namespace Harness.UI.Components.Meta;

public enum MetaChatRole
{
    User,
    Agent,
}

/// <summary>One bubble in the Meta Agent simple chat (user, posted message, or queued prompt).</summary>
public readonly record struct MetaChatItem(
    string Key,
    MetaChatRole Role,
    string Text,
    bool Pending,
    Guid? QueuedId);

/// <summary>
/// Newest root Meta Agent session for a work-directory list (roots-only <see cref="DysonSessionSummary"/>).
/// </summary>
public static class MetaAgentSessionLocator
{
    public static DysonSessionSummary? SelectExisting(IReadOnlyList<DysonSessionSummary> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        DysonSessionSummary? best = null;
        foreach (var session in sessions)
        {
            if (!string.Equals(session.AgentMode, DysonAgentModes.MetaAgent, StringComparison.OrdinalIgnoreCase))
                continue;
            if (session.ParentSessionId is not null)
                continue;

            if (best is null
                || session.LastActivityUtc > best.LastActivityUtc
                || (session.LastActivityUtc == best.LastActivityUtc && session.CreatedUtc > best.CreatedUtc))
            {
                best = session;
            }
        }

        return best;
    }
}

/// <summary>
/// Plain-text Meta Agent transcript: user instructions, injected comments, and DisplayInfo posts.
/// Tool calls, reasoning, and harness turns stay out.
/// </summary>
public static class MetaChatItems
{
    public static IReadOnlyList<MetaChatItem> Build(
        IReadOnlyList<DysonAgentTurn> turns,
        IReadOnlyList<QueuedPrompt> queued)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(queued);

        var items = new List<MetaChatItem>(turns.Count + queued.Count);
        foreach (var turn in turns)
        {
            if (turn.Kind == DysonAgentTurnKind.Normal
                && !string.IsNullOrWhiteSpace(turn.Instruction))
            {
                items.Add(new MetaChatItem(
                    $"u:{turn.Id:D}",
                    MetaChatRole.User,
                    turn.Instruction,
                    Pending: false,
                    QueuedId: null));
            }

            var commentIndex = 0;
            foreach (var segment in turn.ReasoningLog)
            {
                if (segment.Kind != DysonReasoningSegmentKind.UserComment)
                    continue;
                if (string.IsNullOrWhiteSpace(segment.Text))
                    continue;

                items.Add(new MetaChatItem(
                    $"c:{turn.Id:D}:{commentIndex}",
                    MetaChatRole.User,
                    segment.Text,
                    Pending: false,
                    QueuedId: null));
                commentIndex++;
            }

            if (turn.Kind == DysonAgentTurnKind.DisplayInfo
                && !string.IsNullOrWhiteSpace(turn.AssistantText))
            {
                items.Add(new MetaChatItem(
                    $"a:{turn.Id:D}",
                    MetaChatRole.Agent,
                    turn.AssistantText,
                    Pending: false,
                    QueuedId: null));
            }
        }

        foreach (var queuedPrompt in queued)
        {
            var text = queuedPrompt.Text;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            items.Add(new MetaChatItem(
                $"q:{queuedPrompt.Id:D}",
                MetaChatRole.User,
                text,
                Pending: true,
                queuedPrompt.Id));
        }

        return items;
    }
}
