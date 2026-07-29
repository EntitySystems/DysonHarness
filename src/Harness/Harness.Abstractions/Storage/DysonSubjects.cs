namespace DysonHarness;

/// <summary>Well-known persistence subject ids (distinct from <see cref="DysonWorkspaceSubjects"/>).</summary>
public static class DysonSubjects
{
    /// <summary>Desktop / local-host fixed subject.</summary>
    public const string Local = "local";

    /// <summary>
    /// Sentinel for shared model providers visible to every subject.
    /// Not a real <c>subjects</c> row and never a cookie subject.
    /// </summary>
    public const string Shared = "shared";
}
