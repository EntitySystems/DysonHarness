namespace DysonHarness;

/// <summary>Maps live turns ↔ persisted turn rows (tool-state JSON included).</summary>
public static class DysonTurnPersistence
{
    public static DysonTurnEntity ToEntity(
        DysonAgentTurn turn,
        Guid sessionId,
        int sequence,
        DateTime? createdUtc = null,
        DateTime? completedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(turn);

        return new DysonTurnEntity
        {
            Id = turn.Id == Guid.Empty ? Guid.NewGuid() : turn.Id,
            SessionId = sessionId,
            Sequence = sequence,
            Kind = turn.Kind,
            AgentTitle = turn.AgentTitle,
            Instruction = turn.Instruction,
            AssistantText = turn.AssistantText,
            ToolStateJson = DysonTurnToolStateSerializer.CaptureFromTurn(turn),
            ToolHistoryOptimized = turn.ToolHistoryOptimized,
            CompactToolHistory = turn.CompactToolHistory,
            CreatedUtc = createdUtc ?? (turn.StartedUtc != default ? turn.StartedUtc : DateTime.UtcNow),
            CompletedUtc = completedUtc ?? turn.CompletedUtc,
        };
    }

    public static DysonSessionLogEntry CreateTurnStartedLog(Guid sessionId, DysonAgentTurn turn) =>
        DysonSessionLogPayload.CreateEntry(
            sessionId,
            DysonSessionLogKind.TurnStarted,
            new DysonSessionLogTurnStarted(turn.Id, turn.Kind, turn.AgentTitle),
            turnId: turn.Id);

    public static DysonSessionLogEntry CreateToolCallLog(
        Guid sessionId,
        Guid turnId,
        DysonTrackedToolCall tracked,
        DysonSessionLogKind kind)
    {
        ArgumentNullException.ThrowIfNull(tracked);

        var stage = kind switch
        {
            DysonSessionLogKind.ToolCallQueued => "Queued",
            DysonSessionLogKind.ToolCallWorking => "Working",
            DysonSessionLogKind.ToolCallCompleted => "Completed",
            DysonSessionLogKind.ToolCallFailed => "Failed",
            _ => tracked.Status.ToString(),
        };

        var payload = new DysonSessionLogToolCall(
            turnId,
            tracked.Call.CallId,
            tracked.Call.ToolName,
            stage,
            ArgumentsJson: tracked.Call.ArgumentsJson,
            ResultContent: tracked.Result?.Content,
            IsError: tracked.Result?.IsError);

        return DysonSessionLogPayload.CreateEntry(sessionId, kind, payload, turnId: turnId);
    }

    public static DysonSessionLogKind? LogKindForToolStatus(DysonToolCallStatus status) => status switch
    {
        DysonToolCallStatus.Queued => DysonSessionLogKind.ToolCallQueued,
        DysonToolCallStatus.Working => DysonSessionLogKind.ToolCallWorking,
        DysonToolCallStatus.Completed => DysonSessionLogKind.ToolCallCompleted,
        DysonToolCallStatus.Failed => DysonSessionLogKind.ToolCallFailed,
        _ => null,
    };
}
