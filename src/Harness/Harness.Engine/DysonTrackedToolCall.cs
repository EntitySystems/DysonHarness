namespace DysonHarness;

/// <summary>
/// Per-call UI/runtime row on a turn (Queued → Working → Completed|Failed).
/// </summary>
public sealed class DysonTrackedToolCall
{
    private DysonAgentTurn? _owner;

    public required DysonToolCall Call { get; init; }
    public DysonToolCallStatus Status { get; private set; } = DysonToolCallStatus.Queued;
    public DysonToolCallResult? Result { get; private set; }

    internal void Attach(DysonAgentTurn owner) => _owner = owner;

    internal void SetWorking()
    {
        var previous = Status;
        if (IsTerminal(previous) || previous == DysonToolCallStatus.Working)
            return;

        Status = DysonToolCallStatus.Working;
        _owner?.NotifyStatusChanged(this, previous);
    }

    internal void SetCompleted(DysonToolCallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var previous = Status;
        if (IsTerminal(previous))
            return;

        Result = result;
        Status = DysonToolCallStatus.Completed;
        _owner?.NotifyStatusChanged(this, previous);
    }

    internal void SetFailed(DysonToolCallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var previous = Status;
        if (IsTerminal(previous))
            return;

        Result = result;
        Status = DysonToolCallStatus.Failed;
        _owner?.NotifyStatusChanged(this, previous);
    }

    private static bool IsTerminal(DysonToolCallStatus status) =>
        status is DysonToolCallStatus.Completed or DysonToolCallStatus.Failed;
}
