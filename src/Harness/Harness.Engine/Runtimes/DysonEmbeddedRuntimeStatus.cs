namespace DysonHarness;

/// <summary>Probe result for a pinned Node or Python runtime.</summary>
public sealed record DysonEmbeddedRuntimeStatus(
    DysonEmbeddedRuntimeKind Kind,
    string DisplayName,
    string PinnedVersion,
    bool DownloadSupported,
    bool IsInstalled,
    string? ExecutablePath,
    string? Note);
