namespace DysonHarness;

/// <summary>Captured result of a shell process run.</summary>
public sealed class DysonShellRunResult
{
    public required int ExitCode { get; init; }
    public required string Stdout { get; init; }
    public required string Stderr { get; init; }
    public bool TimedOut { get; init; }
    public bool StdoutTruncated { get; init; }
    public bool StderrTruncated { get; init; }

    /// <summary>
    /// Long-running shell id when the run timed out or left its pipes held and was adopted into
    /// <see cref="DysonLongRunningShellRegistry"/> instead of being killed or cut off.
    /// </summary>
    public int? PromotedShellId { get; init; }
}

/// <summary>
/// Where <see cref="DysonWindowsShell.ExecuteWithPathAsync"/> adopts a run that times out or exits with its pipes
/// still held. Null (other callers) keeps kill-on-timeout and return-at-exit.
/// </summary>
public sealed record DysonShellPromotion(Guid WorkDirectoryId, string ShellName);
