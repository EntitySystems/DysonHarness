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
    /// <summary>UI-only chrome (empty-state CTAs); omitted from provider transcripts.</summary>
    DisplayInfo = 11,
    /// <summary>
    /// Mode boundary: completed immediately, no inference. Included in provider transcripts
    /// as a short harness user message; modes encoded in <c>Instruction</c> (<c>From→To</c>).
    /// </summary>
    ModeSwitch = 12,
    /// <summary>
    /// Context budget hygiene: agent may DropTurnContext on turns older than the last four
    /// when estimated outgoing tokens exceed the session max target.
    /// </summary>
    DropContext = 13,
}
