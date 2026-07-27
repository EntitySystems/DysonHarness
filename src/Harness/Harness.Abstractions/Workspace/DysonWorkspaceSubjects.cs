namespace DysonHarness;

/// <summary>Well-known <see cref="IDysonWorkspaceFileSystem"/> subject ids for <c>InitializeAsync</c>.</summary>
public static class DysonWorkspaceSubjects
{
    /// <summary>Local / SMB / UNC path-backed workspace (no remote auth).</summary>
    public const string LocalFs = "local_fs";
}
