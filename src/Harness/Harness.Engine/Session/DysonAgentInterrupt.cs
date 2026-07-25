namespace DysonHarness;

public enum DysonAgentInterruptKind
{
    SubagentCompleted = 1,
    SubagentStopped = 2,
    SubagentFailed = 3,
    /// <summary>Child triggered a parent event (general or askQuestion); host shows UI / may auto-turn.</summary>
    SubagentEvent = 4,
    /// <summary>A subscribed long-running shell reached a terminal state; host drains a ShellExited turn.</summary>
    LongRunningShellExited = 5,
}

public sealed class DysonAgentInterrupt
{
    public required DysonAgentInterruptKind Kind { get; init; }
    /// <summary>Subagent runtime id; 0 for non-subagent interrupts (e.g. long-running shell).</summary>
    public required int SubagentId { get; init; }
    /// <summary>Child session durable id when known (for host registry / auto-turn).</summary>
    public Guid? PersistenceId { get; init; }
    public string? Summary { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Set when <see cref="Kind"/> is <see cref="DysonAgentInterruptKind.SubagentEvent"/>.</summary>
    public Guid? EventId { get; init; }

    /// <summary>Event kind string (e.g. askQuestion) when <see cref="Kind"/> is SubagentEvent.</summary>
    public string? EventKind { get; init; }

    /// <summary>Raw event payload when <see cref="Kind"/> is SubagentEvent.</summary>
    public string? Payload { get; init; }

    /// <summary>Workdir of the shell when <see cref="Kind"/> is LongRunningShellExited.</summary>
    public Guid? WorkDirectoryId { get; init; }

    /// <summary>Registry id when <see cref="Kind"/> is LongRunningShellExited.</summary>
    public int? LongRunningShellId { get; init; }

    /// <summary>Process exit code when known (LongRunningShellExited).</summary>
    public int? ExitCode { get; init; }

    /// <summary><c>success</c> | <c>failure</c> | <c>cancelled</c> for LongRunningShellExited.</summary>
    public string? ShellOutcome { get; init; }

    /// <summary>Max chars for auto-read tail when building the ShellExited Instruction (default 8000).</summary>
    public int IncludeTailMaxChars { get; init; } = 8000;
}
