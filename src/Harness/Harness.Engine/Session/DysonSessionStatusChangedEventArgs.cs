namespace DysonHarness;

public sealed class DysonSessionStatusChangedEventArgs : EventArgs
{
    public required DysonSessionStatus PreviousStatus { get; init; }
    public required DysonSessionStatus Status { get; init; }
    public string? Summary { get; init; }
}
