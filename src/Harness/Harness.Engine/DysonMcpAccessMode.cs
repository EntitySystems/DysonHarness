namespace DysonHarness;

public enum DysonMcpAccessMode
{
    /// <summary>Tool calls run with full access; no allowlist gating.</summary>
    FullAccess = 0,

    /// <summary>Tool calls go through the in-process auto-review proxy; no allowlist.</summary>
    AutoReview = 1,
}
