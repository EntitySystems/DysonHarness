using DysonHarness;

namespace Harness.UI.Demo;

/// <summary>
/// Host-bus request to open the file viewer with caller-supplied content (no disk read).
/// Publish on <c>Host.BusScopeKey</c>; the host prepares asynchronously.
/// </summary>
public sealed record DysonFileViewerOpenRequestedEvent(
    string RelativePath,
    string Content,
    IReadOnlyList<DysonFileViewerAction> Actions) : IDysonMessageBusEvent;

/// <summary>
/// Host-bus paint signal for the file viewer overlay. Immediate (not Overlay coalescer).
/// <paramref name="Viewer"/> is null when the overlay is closed.
/// </summary>
public sealed record DysonFileViewerChangedEvent(
    int Epoch,
    DysonFileViewerState? Viewer) : IDysonMessageBusEvent;
