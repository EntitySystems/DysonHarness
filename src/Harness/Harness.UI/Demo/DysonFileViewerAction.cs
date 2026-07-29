namespace Harness.UI.Demo;

/// <summary>Footer CTA for the file viewer overlay (host-supplied).</summary>
public sealed class DysonFileViewerAction
{
    public required string Label { get; init; }
    public required Func<Task> Invoke { get; init; }
    public bool IsPrimary { get; init; }
}
