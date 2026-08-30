using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DysonHarness;

/// <summary>Ordered reasoning / interim-text segment kinds for a turn's thinking history.</summary>
public enum DysonReasoningSegmentKind
{
    Thought = 0,
    InterimText = 1,
}

/// <summary>
/// One entry in a turn's reasoning log (UI + DB only; never injected into model transcripts).
/// </summary>
public sealed record DysonReasoningSegment(
    DysonReasoningSegmentKind Kind,
    string Text,
    int RoundIndex);

/// <summary>JSON serialize/restore helpers for <see cref="DysonAgentTurn.ReasoningLog"/>.</summary>
public static class DysonReasoningLogSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string? Serialize(IReadOnlyList<DysonReasoningSegment> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (log.Count == 0)
            return null;

        return JsonSerializer.Serialize(log, Options);
    }

    public static List<DysonReasoningSegment> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<DysonReasoningSegment>>(json, Options) ?? [];
    }

    /// <summary>
    /// Restores the log from JSON; when empty but legacy <paramref name="reasoningText"/> is set,
    /// synthesizes a single Thought segment (round 0).
    /// </summary>
    public static List<DysonReasoningSegment> DeserializeOrSynthesize(string? json, string? reasoningText)
    {
        var log = Deserialize(json);
        if (log.Count > 0)
            return log;

        if (string.IsNullOrWhiteSpace(reasoningText))
            return [];

        return [new DysonReasoningSegment(DysonReasoningSegmentKind.Thought, reasoningText, RoundIndex: 0)];
    }

    /// <summary>Denormalized join of Thought segments only (blank InterimText ignored).</summary>
    public static string? JoinThoughtTexts(IEnumerable<DysonReasoningSegment> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        StringBuilder? sb = null;
        foreach (var segment in log)
        {
            if (segment.Kind != DysonReasoningSegmentKind.Thought)
                continue;
            if (string.IsNullOrWhiteSpace(segment.Text))
                continue;

            sb ??= new StringBuilder();
            if (sb.Length > 0)
                sb.Append("\n\n");
            sb.Append(segment.Text);
        }

        return sb?.Length > 0 ? sb.ToString() : null;
    }
}

/// <summary>
/// UI expand/collapse rules for ordered Thought + InterimText slots in the thinking history.
/// </summary>
public static class DysonReasoningHistoryUi
{
    /// <summary>
    /// Latest Thought/InterimText slot stays open while the turn has no assistant body yet
    /// (final <c>AssistantText</c> or streaming preview) and/or while reasoning is still
    /// streaming. Prior slots stay collapsed. Once an assistant body exists and reasoning is
    /// not streaming, all slots default collapsed.
    /// </summary>
    public static bool ShouldExpandSegment(
        int segmentOrdinal,
        int segmentCount,
        bool hasAssistantBody,
        bool isReasoningStreaming)
    {
        if (segmentCount <= 0 || segmentOrdinal < 0 || segmentOrdinal >= segmentCount)
            return false;

        if (hasAssistantBody && !isReasoningStreaming)
            return false;

        return segmentOrdinal == segmentCount - 1;
    }

    /// <summary>
    /// True while a non-empty reasoning preview is streaming (the live trailing
    /// <c>Thinking N</c> slot in thinking history).
    /// </summary>
    public static bool IsLiveReasoningStreaming(DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return turn.IsReasoningStreaming && !string.IsNullOrEmpty(turn.ReasoningStreamingPreview);
    }

    /// <summary>
    /// Display title for a reasoning segment: Markdown H1 when present, otherwise
    /// <paramref name="fallback"/>. Body is the remainder after the H1 line, or the
    /// full text when there is no title. Presentation-only; never mutates the turn.
    /// </summary>
    public static (string Title, string Body) SplitSegmentTitle(string text, string fallback)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fallback);

        var parsed = DysonAgentTurn.TryParseAgentTitle(text);
        if (parsed.IsSuccess && !string.IsNullOrWhiteSpace(parsed.Value.Title))
            return (parsed.Value.Title!, parsed.Value.Body);

        return (fallback, text);
    }

    /// <summary>
    /// Thought fallback matching TurnBlock ordinals: <c>Thinking</c> for a single
    /// committed thought with no live slot; <c>Thinking N</c> when multiple thoughts
    /// exist or a live trailing thought is streaming.
    /// </summary>
    public static string ThoughtFallback(int thoughtOrdinal, int thoughtCount, bool liveStreaming)
        => thoughtCount > 1 || liveStreaming
            ? $"Thinking {thoughtOrdinal + 1}"
            : "Thinking";

    /// <summary>
    /// Interim-text fallback matching TurnBlock ordinals: <c>Note</c> or <c>Note N</c>.
    /// </summary>
    public static string InterimFallback(int interimOrdinal, int interimCount)
        => interimCount > 1
            ? $"Note {interimOrdinal + 1}"
            : "Note";

    /// <summary>Live trailing thought label: <c>Thinking</c> or next <c>Thinking N</c>.</summary>
    public static string LiveThoughtTitle(int thoughtCount)
        => thoughtCount > 0 ? $"Thinking {thoughtCount + 1}" : "Thinking";

    /// <summary>
    /// Latest safe visible step label for a turn (parent-card / thinking-history titles).
    /// Precedence: finalized <see cref="DysonAgentTurn.AgentTitle"/>; live
    /// <c>Thinking</c>/<c>Thinking N</c> while reasoning streams (never the preview body);
    /// last non-empty <see cref="DysonAgentTurn.ReasoningLog"/> segment (H1 or ordinal fallback);
    /// legacy <see cref="DysonAgentTurn.ReasoningText"/> as <c>Thinking</c> (or its H1);
    /// otherwise null. Read-only; does not persist or inject reasoning.
    /// </summary>
    public static string? TryGetLatestStepTitle(DysonAgentTurn? turn)
    {
        if (turn is null)
            return null;

        if (!string.IsNullOrWhiteSpace(turn.AgentTitle))
            return turn.AgentTitle;

        var log = turn.ReasoningLog;
        var liveStreaming = IsLiveReasoningStreaming(turn);
        var thoughtCount = 0;
        var interimCount = 0;
        foreach (var segment in log)
        {
            if (segment.Kind == DysonReasoningSegmentKind.Thought)
                thoughtCount++;
            else if (segment.Kind == DysonReasoningSegmentKind.InterimText
                     && !string.IsNullOrWhiteSpace(segment.Text))
                interimCount++;
        }

        if (liveStreaming)
            return LiveThoughtTitle(thoughtCount);

        DysonReasoningSegment? last = null;
        var lastThoughtOrdinal = -1;
        var lastInterimOrdinal = -1;
        var thoughtOrdinal = 0;
        var interimOrdinal = 0;
        foreach (var segment in log)
        {
            if (segment.Kind == DysonReasoningSegmentKind.Thought)
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                {
                    last = segment;
                    lastThoughtOrdinal = thoughtOrdinal;
                }

                thoughtOrdinal++;
            }
            else if (segment.Kind == DysonReasoningSegmentKind.InterimText
                     && !string.IsNullOrWhiteSpace(segment.Text))
            {
                last = segment;
                lastInterimOrdinal = interimOrdinal;
                interimOrdinal++;
            }
        }

        if (last is not null)
        {
            var fallback = last.Kind == DysonReasoningSegmentKind.Thought
                ? ThoughtFallback(lastThoughtOrdinal, thoughtCount, liveStreaming: false)
                : InterimFallback(lastInterimOrdinal, interimCount);
            return SplitSegmentTitle(last.Text, fallback).Title;
        }

        if (!string.IsNullOrWhiteSpace(turn.ReasoningText))
            return SplitSegmentTitle(turn.ReasoningText, "Thinking").Title;

        return null;
    }
}
