namespace Harness.UI.Demo;

/// <summary>Host state for the chat-preserving file viewer overlay.</summary>
public sealed class DysonFileViewerState
{
    public required string RelativePath { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public required bool IsMarkdown { get; init; }
    public string? Error { get; init; }
}
