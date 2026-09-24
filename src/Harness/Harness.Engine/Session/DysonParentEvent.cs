namespace DysonHarness;

public enum DysonParentEventStatus
{
    Pending = 0,
    Addressed = 1,
    Cancelled = 2,
}

/// <summary>
/// Inbound child→parent event awaiting <see cref="DysonAgentSession.RespondToSubagentEvent"/>.
/// Not persisted across process restart.
/// </summary>
public sealed class DysonParentEvent
{
    public required Guid EventId { get; init; }
    public required int SubagentId { get; init; }
    public Guid? PersistenceId { get; init; }
    public required string Kind { get; init; }
    public required string Payload { get; init; }
    public DysonParentEventStatus Status { get; set; } = DysonParentEventStatus.Pending;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Forgotten meta auto-turns for this process. Not persisted.</summary>
    public int ForgottenAutoTurns { get; set; }

    /// <summary>Root posted DisplayInfo and is waiting on the user. Not persisted. Cleared when the event leaves Pending.</summary>
    public bool AwaitingUserAnswer { get; set; }

    internal TaskCompletionSource<Result<string, string>> ReplyTcs { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
