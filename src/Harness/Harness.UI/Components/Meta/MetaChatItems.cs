using System.IO;
using DysonHarness;
using Harness.UI.Demo;

namespace Harness.UI.Components.Meta;

public enum MetaChatRole
{
    User,
    Agent,
}

/// <summary>Image thumb on a meta user bubble. <see cref="Src"/> is a remote URL or a data URL.</summary>
public readonly record struct MetaChatThumb(string Src, string Alt);

/// <summary>One bubble in the Meta Agent simple chat (user, posted message, or queued prompt).</summary>
public readonly record struct MetaChatItem(
    string Key,
    MetaChatRole Role,
    string Text,
    bool Pending,
    Guid? QueuedId)
{
    /// <summary>User-image thumbs. Empty for comments, agent posts, and queued text.</summary>
    public IReadOnlyList<MetaChatThumb> Thumbs { get; init; } = [];

    /// <summary>File-name chips parsed from local paths. Never a directory or URL.</summary>
    public IReadOnlyList<string> FileNames { get; init; } = [];

    /// <summary>Buttons on an agent DisplayInfo bubble. Empty everywhere else.</summary>
    public IReadOnlyList<DysonConversationAction> Actions { get; init; } = [];

    /// <summary>Source turn for an agent DisplayInfo bubble. Null for user and queued rows.</summary>
    public Guid? TurnId { get; init; }

    /// <summary>Visualization opened from a DisplayInfo bubble. Null everywhere else.</summary>
    public Guid? VisualizationId { get; init; }
}

/// <summary>Click on a meta-chat conversation action button. Index is into that turn's actions.</summary>
public readonly record struct MetaChatActionClick(Guid TurnId, int Index);

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
            if (turn.Kind == DysonAgentTurnKind.Normal)
            {
                var text = turn.Instruction ?? "";
                var thumbs = ThumbsFrom(turn);
                var fileNames = FileNamesFromHiddenInstruction(turn.HiddenInstruction);
                if (!string.IsNullOrWhiteSpace(text)
                    || thumbs.Count > 0
                    || !string.IsNullOrWhiteSpace(turn.HiddenInstruction))
                {
                    items.Add(new MetaChatItem(
                        $"u:{turn.Id:D}",
                        MetaChatRole.User,
                        text,
                        Pending: false,
                        QueuedId: null)
                    {
                        Thumbs = thumbs,
                        FileNames = fileNames,
                    });
                }
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
                    QueuedId: null)
                {
                    Actions = turn.ConversationActions,
                    TurnId = turn.Id,
                    VisualizationId = turn.VisualizationId,
                });
            }
        }

        foreach (var queuedPrompt in queued)
        {
            if (!QueuedRowVisible(queuedPrompt))
                continue;

            items.Add(new MetaChatItem(
                $"q:{queuedPrompt.Id:D}",
                MetaChatRole.User,
                queuedPrompt.Text ?? "",
                Pending: true,
                queuedPrompt.Id));
        }

        return items;
    }

    /// <summary>
    /// Words come from <see cref="QueuedPrompt.Text"/>. <see cref="QueuedPrompt.HasAttachments"/>
    /// keeps an attachment-only queue row (empty text) visible.
    /// </summary>
    private static bool QueuedRowVisible(QueuedPrompt queuedPrompt) =>
        !string.IsNullOrWhiteSpace(queuedPrompt.Text) || queuedPrompt.HasAttachments;

    private static IReadOnlyList<MetaChatThumb> ThumbsFrom(DysonAgentTurn turn)
    {
        if (turn.UserImages.Count == 0)
            return [];

        var thumbs = new List<MetaChatThumb>(turn.UserImages.Count);
        foreach (var image in turn.UserImages)
        {
            string? src = null;
            if (!string.IsNullOrWhiteSpace(image.RemoteUrl))
                src = image.RemoteUrl;
            else if (!string.IsNullOrWhiteSpace(image.Base64Data))
                src = $"data:{image.MimeType};base64,{image.Base64Data}";

            if (src is null)
                continue;

            thumbs.Add(new MetaChatThumb(src, image.FileName));
        }

        return thumbs;
    }

    /// <summary>
    /// File-name chips from the <c>Attached paths:</c> block. Stops at a later
    /// <c>Attached …:</c> header so a URL block is never a chip.
    /// </summary>
    private static IReadOnlyList<string> FileNamesFromHiddenInstruction(string? hidden)
    {
        if (string.IsNullOrWhiteSpace(hidden))
            return [];

        var names = new List<string>();
        var inPaths = false;
        foreach (var raw in hidden.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0)
                continue;

            if (line.StartsWith("Attached ", StringComparison.OrdinalIgnoreCase)
                && line.EndsWith(':'))
            {
                inPaths = line.Equals("Attached paths:", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inPaths || !line.StartsWith("- ", StringComparison.Ordinal))
                continue;

            var path = line[2..].Trim();
            if (path.Length == 0)
                continue;

            var name = Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar));
            names.Add(string.IsNullOrWhiteSpace(name) ? path : name);
        }

        return names;
    }
}
