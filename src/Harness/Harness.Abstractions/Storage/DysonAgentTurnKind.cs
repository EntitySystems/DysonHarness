namespace DysonHarness;

/// <summary>Persisted / runtime turn kind discriminator (mirrored for storage contracts).</summary>
public enum DysonAgentTurnKind
{
    Normal = 0,
    ExpandThoughtProcess = 1,
    TaskCompletionConfirm = 2,
    Continuation = 3,
    ReportSummary = 4,
    InitializeSession = 5,
    PlanResult = 6,
    BeginBuildPlan = 7,
    SubagentReportProcessing = 8,
    ShellExited = 9,
    RethinkToolUsage = 10,
}
