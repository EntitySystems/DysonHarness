namespace DysonHarness;

/// <summary>Filesystem change kinds surfaced by <see cref="IDysonWorkspaceChangeWatcher"/>.</summary>
public enum DysonWorkspaceChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed,
}
