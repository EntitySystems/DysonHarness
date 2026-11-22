using System.IO;
using System.Text.Json.Nodes;
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

    /// <summary>Question card on a DisplayInfo bubble. Null everywhere else.</summary>
    public DysonUserQuestion? UserQuestion { get; init; }

    /// <summary>Secrets card on a DisplayInfo bubble. Null everywhere else. Never a value.</summary>
    public DysonSecureDataRequest? SecureDataRequest { get; init; }

    /// <summary>A2UI surface on a DisplayInfo bubble (may be a stub with only LastError). Null everywhere else.</summary>
    public DysonA2uiSurface? A2uiSurface { get; init; }

    /// <summary>
    /// Questions placed by this item's QuestionSlots, looked up in all turns. Null value: withdrawn or unknown.
    /// </summary>
    public IReadOnlyDictionary<Guid, DysonUserQuestion?> EmbeddedQuestions { get; init; } = MetaChatItems.NoEmbeddedQuestions;
}

/// <summary>Click on a meta-chat conversation action button. Index is into that turn's actions.</summary>
public readonly record struct MetaChatActionClick(Guid TurnId, int Index);

/// <summary>Click on an A2UI ActionButton. <see cref="Func"/> is a built-in key from the validated surface.</summary>
public readonly record struct MetaChatFuncClick(Guid TurnId, string Func);

/// <summary>
/// Button click on an A2UI surface. The host resolves it against the stored surface (or, for
/// <c>dyson_question_*</c>, the server-rebuilt card); only the component id and local model come from here.
/// </summary>
public readonly record struct MetaChatA2uiAction(Guid TurnId, string SurfaceId, string SourceComponentId, JsonObject LocalModel);

/// <summary>Per-surface busy flag and host error for A2UI actions in the meta feed, keyed by surface id.</summary>
public sealed class MetaChatA2uiState
{
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);

    /// <summary>Bumps on every change so views holding this state know to re-render.</summary>
    public int Version { get; private set; }

    public bool IsBusy(string surfaceId) => _busy.Contains(surfaceId);

    public string? ErrorFor(string surfaceId) => _errors.GetValueOrDefault(surfaceId);

    /// <summary>Marks the surface busy and clears its error. False when it is already busy.</summary>
    public bool TryBegin(string surfaceId)
    {
        if (!_busy.Add(surfaceId))
            return false;
        _errors.Remove(surfaceId);
        Version++;
        return true;
    }

    public void End(string surfaceId)
    {
        if (_busy.Remove(surfaceId))
            Version++;
    }

    /// <summary>Empty clears the error.</summary>
    public void NoteError(string surfaceId, string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            _errors.Remove(surfaceId);
        else
            _errors[surfaceId] = error;
        Version++;
    }
}

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
/// Tool calls, reasoning, and harness turns stay out. Root Meta history shows the newest
/// <see cref="DysonMetaChatWindow.MaxInteractions"/> bubbles; queued prompts follow outside the cap.
/// </summary>
public static class MetaChatItems
{
    public static readonly IReadOnlyDictionary<Guid, DysonUserQuestion?> NoEmbeddedQuestions =
        new Dictionary<Guid, DysonUserQuestion?>();

    public static IReadOnlyList<MetaChatItem> Build(
        IReadOnlyList<DysonAgentTurn> turns,
        IReadOnlyList<QueuedPrompt> queued)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(queued);

        var start = DysonMetaChatWindow.DisplayStart(turns);
        var items = new List<MetaChatItem>(turns.Count - start + queued.Count);
        var embedded = EmbeddedQuestionIds(turns, start);
        var questions = embedded.Count == 0 ? null : QuestionsById(turns);
        for (var t = start; t < turns.Count; t++)
        {
            var turn = turns[t];
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
                // A voice utterance is the user's bubble. The prefix is stored on the turn, so reload keeps the side.
                var voiceInput = DysonVoiceMode.IsVoiceInputDisplay(turn);
                // A question placed by a QuestionSlot shows only inside that layout; its own card (and any
                // surface bound to it) hides here. Derived, so deleting the layout brings the card back.
                var embeddedHere = turn.UserQuestion is { } own && embedded.Contains(own.Id);
                // A surface forwarding that embedded question hides with it; the slot draws the derived card.
                var surface = voiceInput || (embeddedHere && turn.A2uiSurface?.QuestionId == turn.UserQuestion!.Id)
                    ? null
                    : turn.A2uiSurface;
                var rendered = surface is { Components.Count: > 0 };
                items.Add(new MetaChatItem(
                    voiceInput ? $"u:{turn.Id:D}" : $"a:{turn.Id:D}",
                    voiceInput ? MetaChatRole.User : MetaChatRole.Agent,
                    turn.AssistantText,
                    Pending: false,
                    QueuedId: null)
                {
                    // The surface wins: a rendered surface's ActionButtons are the actions.
                    Actions = voiceInput || rendered ? [] : turn.ConversationActions,
                    TurnId = voiceInput ? null : turn.Id,
                    VisualizationId = voiceInput ? null : turn.VisualizationId,
                    UserQuestion = voiceInput || embeddedHere ? null : turn.UserQuestion,
                    SecureDataRequest = voiceInput ? null : turn.SecureDataRequest,
                    A2uiSurface = surface,
                    EmbeddedQuestions = rendered && questions is not null
                        ? EmbeddedFor(surface!, questions)
                        : NoEmbeddedQuestions,
                });
            }
        }

        // The window's oldest turn can carry several bubbles; drop its extras past the cap.
        if (items.Count > DysonMetaChatWindow.MaxInteractions)
            items.RemoveRange(0, items.Count - DysonMetaChatWindow.MaxInteractions);

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

    /// <summary>QuestionSlot ids of every rendered surface in the display window.</summary>
    private static HashSet<Guid> EmbeddedQuestionIds(IReadOnlyList<DysonAgentTurn> turns, int start)
    {
        var ids = new HashSet<Guid>();
        for (var t = start; t < turns.Count; t++)
        {
            if (turns[t].Kind == DysonAgentTurnKind.DisplayInfo && turns[t].A2uiSurface is { Components.Count: > 0 } surface)
                ids.UnionWith(DysonA2uiSurfaces.QuestionSlotIds(surface));
        }

        return ids;
    }

    /// <summary>All turns, not just the window: an embedded question may be older than the display window.</summary>
    private static Dictionary<Guid, DysonUserQuestion> QuestionsById(IReadOnlyList<DysonAgentTurn> turns)
    {
        var map = new Dictionary<Guid, DysonUserQuestion>();
        foreach (var turn in turns)
        {
            if (turn.Kind == DysonAgentTurnKind.DisplayInfo && turn.UserQuestion is { } question)
                map[question.Id] = question;
        }

        return map;
    }

    private static IReadOnlyDictionary<Guid, DysonUserQuestion?> EmbeddedFor(
        DysonA2uiSurface surface,
        Dictionary<Guid, DysonUserQuestion> questions)
    {
        var ids = DysonA2uiSurfaces.QuestionSlotIds(surface);
        if (ids.Count == 0)
            return NoEmbeddedQuestions;

        var map = new Dictionary<Guid, DysonUserQuestion?>(ids.Count);
        foreach (var id in ids)
            map[id] = questions.GetValueOrDefault(id);
        return map;
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
