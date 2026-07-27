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
}
