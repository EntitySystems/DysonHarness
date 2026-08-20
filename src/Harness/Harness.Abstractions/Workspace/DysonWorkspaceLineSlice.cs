namespace DysonHarness;

/// <summary>Bounded line window from a workspace file (no full-file load).</summary>
public sealed record DysonWorkspaceLineSlice(
    IReadOnlyList<DysonWorkspaceLine> Lines,
    int StartLine,
    int NextLine,
    bool Truncated,
    long FileLengthBytes,
    bool Tailed);

/// <summary>One line from <see cref="DysonWorkspaceLineSlice"/> (newline stripped).</summary>
public readonly record struct DysonWorkspaceLine(int LineNumber, string Text, bool Clipped);
