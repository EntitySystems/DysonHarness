namespace DysonHarness;

/// <summary>Lifecycle of a workdir-scoped long-running shell process.</summary>
public enum DysonLongRunningShellStatus
{
    Running,
    Exited,
    Aborted,
    CancelRequested,
}
