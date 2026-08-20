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
}
