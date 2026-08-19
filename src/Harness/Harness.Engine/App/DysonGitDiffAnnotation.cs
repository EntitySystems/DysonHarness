namespace DysonHarness;

/// <summary>
/// Kind of a unified-diff hunk relative to the comparable Git baseline (<c>HEAD</c>).
/// </summary>
public enum DysonGitDiffAnnotationKind
{
    /// <summary>
    /// Inserted lines: empty original side, nonempty working-tree side.
    /// </summary>
    Added,

    /// <summary>
    /// Replaced existing lines: both original and working-tree sides nonempty.
    /// </summary>
    Modified,

    /// <summary>
    /// Removed lines: nonempty original side, empty working-tree side.
    /// A count of zero on the modified side means no current-file line is invented.
    /// </summary>
    Deleted,
}

/// <summary>
/// One unified-diff hunk as original (baseline) and modified (working-tree) line ranges.
/// Starts are <strong>one-based</strong>. A start of 0 with a count of 0 means that side is
/// empty (typical for a full-file add or delete). A count of 0 with a positive start means
/// an insertion or deletion after that line, so deletions can be rendered as an anchor
/// without inventing a current-file line. Omitted hunk counts in unified headers are one.
/// </summary>
/// <param name="Kind">Classification from original vs modified line counts.</param>
/// <param name="OriginalStartLine">One-based start in the baseline file; 0 when that side is empty.</param>
/// <param name="OriginalLineCount">Number of baseline lines in the hunk; 0 when that side is empty.</param>
/// <param name="ModifiedStartLine">One-based start in the working tree; 0 when that side is empty.</param>
/// <param name="ModifiedLineCount">Number of working-tree lines in the hunk; 0 when that side is empty.</param>
public sealed record DysonGitDiffAnnotation(
    DysonGitDiffAnnotationKind Kind,
    int OriginalStartLine,
    int OriginalLineCount,
    int ModifiedStartLine,
    int ModifiedLineCount);
