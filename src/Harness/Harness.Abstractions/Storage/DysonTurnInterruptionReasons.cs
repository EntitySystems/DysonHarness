namespace DysonHarness;

/// <summary>
/// Stable persisted turn interruption reason codes (not assistant text; omitted from transcripts).
/// </summary>
public static class DysonTurnInterruptionReasons
{
    /// <summary>
    /// Process restart recovered an unfinished turn without replaying model or tool work.
    /// UI may render: "Interrupted by application restart; no model/tool call was replayed."
    /// </summary>
    public const string ApplicationRestart = "application-restart";
}
