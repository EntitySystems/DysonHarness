namespace DysonHarness;

/// <summary>Args for <see cref="IDysonWorkspaceChangeWatcher.Changed"/>.</summary>
public sealed class DysonWorkspaceChangeEventArgs : EventArgs
{
    public required DysonWorkspaceChangeKind Kind { get; init; }

    /// <summary>Absolute native path of the affected entry.</summary>
    public required string FullPath { get; init; }

    /// <summary>Previous absolute path when <see cref="Kind"/> is <see cref="DysonWorkspaceChangeKind.Renamed"/>.</summary>
    public string? OldFullPath { get; init; }
}
