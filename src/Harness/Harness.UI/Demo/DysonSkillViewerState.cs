namespace Harness.UI.Demo;

/// <summary>Host state for the skill markdown viewer overlay.</summary>
public sealed class DysonSkillViewerState
{
    public required string DisplayName { get; init; }
    public string? ResolvedPath { get; init; }
    public required string Markdown { get; init; }
}
