using System.Text.Json;
using System.Text.Json.Serialization;

namespace DysonHarness;

/// <summary>Serializable snapshot of a turn's tool calls / tracked status / response log.</summary>
public sealed class DysonTurnToolState
{
    public List<DysonToolCall> ToolCalls { get; set; } = [];
    public List<DysonPersistedTrackedToolCall> Tracked { get; set; } = [];
    public List<DysonToolCallResult> ResponseLog { get; set; } = [];
}

public sealed class DysonPersistedTrackedToolCall
{
    public required string CallId { get; init; }
    public DysonToolCallStatus Status { get; init; }
    public DysonToolCallResult? Result { get; init; }
}

public static class DysonTurnToolStateSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize(DysonTurnToolState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return JsonSerializer.Serialize(SanitizeGeneratedImageArtifacts(state), Options);
    }

    public static DysonTurnToolState Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return new DysonTurnToolState();

        var state = JsonSerializer.Deserialize<DysonTurnToolState>(json, Options) ?? new DysonTurnToolState();
        return SanitizeGeneratedImageArtifacts(state);
    }

    public static string CaptureFromTurn(DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        // Completed turns: slim RemoteUrl images (drop JPEG bytes) or strip the attachment (legacy).
        var stripAttachments = !string.IsNullOrEmpty(turn.AssistantText);
        if (stripAttachments)
            turn.ClearBinaryAttachments();

        var state = new DysonTurnToolState
        {
            ToolCalls = [.. turn.ToolCalls],
            Tracked =
            [
                .. turn.TrackedToolCalls.Select(t => new DysonPersistedTrackedToolCall
                {
                    CallId = t.Call.CallId,
                    Status = t.Status,
                    Result = t.Result is null
                        ? null
                        : stripAttachments
                            ? t.Result.ForPersistence()
                            : t.Result,
                }),
            ],
            ResponseLog =
            [
                .. turn.ResponseLog.Select(r =>
                    stripAttachments ? r.ForPersistence() : r),
            ],
        };

        return Serialize(state);
    }

    public static void ApplyToTurn(DysonAgentTurn turn, string? toolStateJson)
    {
        ArgumentNullException.ThrowIfNull(turn);

        var state = Deserialize(toolStateJson);
        turn.ToolCalls.Clear();
        turn.ToolCalls.AddRange(state.ToolCalls);
        turn.RestoreTrackedCalls(state.Tracked);
        turn.RestoreResponseLog(state.ResponseLog);
    }

    private static DysonTurnToolState SanitizeGeneratedImageArtifacts(DysonTurnToolState state)
    {
        state.ResponseLog = [.. state.ResponseLog.Select(SanitizeResult)];
        state.Tracked =
        [
            .. state.Tracked.Select(tracked => new DysonPersistedTrackedToolCall
            {
                CallId = tracked.CallId,
                Status = tracked.Status,
                Result = tracked.Result is null ? null : SanitizeResult(tracked.Result),
            }),
        ];
        return state;
    }

    private static DysonToolCallResult SanitizeResult(DysonToolCallResult result)
    {
        var artifacts = (result.GeneratedImageArtifacts ?? [])
            .Select(DysonGeneratedImageArtifact.TryRehydrate)
            .Where(artifact => !artifact.IsError)
            .Select(artifact => artifact.Value)
            .ToArray();

        return new DysonToolCallResult
        {
            CallId = result.CallId,
            ToolName = result.ToolName,
            Stage = result.Stage,
            IsError = result.IsError,
            Content = result.Content,
            BinaryAttachment = result.BinaryAttachment,
            HtmlVisualization = result.HtmlVisualization,
            GeneratedImageArtifacts = artifacts,
            EndsCurrentTurn = result.EndsCurrentTurn,
            CompletedAt = result.CompletedAt,
        };
    }
}
