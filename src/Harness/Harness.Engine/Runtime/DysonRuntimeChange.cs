namespace DysonHarness;

/// <summary>Low-information runtime mutation so a circuit facade can refresh after missed signals.</summary>
public enum DysonRuntimeChangeKind
{
    SessionGraph = 0,
    Busy = 1,
    Queue = 2,
    Error = 3,
    Recovery = 4,
}

/// <summary>
/// Subject-scoped change notification. <see cref="SessionId"/> is null for runtime-wide events
/// (for example the current runtime error).
/// </summary>
public sealed class DysonRuntimeChange : EventArgs
{
    public required string SubjectId { get; init; }

    public Guid? SessionId { get; init; }

    public DysonRuntimeChangeKind Kind { get; init; }

    public long Version { get; init; }
}
