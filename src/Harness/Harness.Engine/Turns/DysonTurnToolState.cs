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

    public static string Serialize(DysonTurnToolState state) =>
        JsonSerializer.Serialize(state, Options);

    public static DysonTurnToolState Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return new DysonTurnToolState();

        return JsonSerializer.Deserialize<DysonTurnToolState>(json, Options) ?? new DysonTurnToolState();
    }

    public static string CaptureFromTurn(DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        // Completed turns: drop BinaryAttachment so SQLite stays small (ack JSON remains).
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
                            ? t.Result.WithoutBinaryAttachment()
                            : t.Result,
                }),
            ],
            ResponseLog =
            [
                .. turn.ResponseLog.Select(r =>
                    stripAttachments ? r.WithoutBinaryAttachment() : r),
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
}
