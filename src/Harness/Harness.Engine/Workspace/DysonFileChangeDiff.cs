namespace DysonHarness;

/// <summary>One rendered diff line for the Change Stream modal.</summary>
public enum DysonFileChangeLineKind
{
    Context,
    Removed,
    Added,
}

/// <summary>One line of a Change Stream diff, in display order.</summary>
public readonly record struct DysonFileChangeLine(DysonFileChangeLineKind Kind, string Text);

/// <summary>
/// One changed hunk: common prefix/suffix trimmed, one context line on each side.
/// Null when the texts are identical after EOL normalization.
/// </summary>
public readonly record struct DysonFileChangeHunk(
    int StartLine,
    string? ContextBefore,
    string? RemovedText,
    string? AddedText,
    string? ContextAfter,
    bool Truncated);

/// <summary>Pure Change Stream diff. No I/O.</summary>
public static class DysonFileChangeDiff
{
    public const int TextCap = 64 * 1024;
    public const int ContextCap = 500;

    /// <summary>
    /// Single-hunk diff. <paramref name="before"/> null means the file was created.
    /// Returns null when nothing changed after EOL normalization.
    /// ponytail: prefix/suffix trim, not a Myers diff — two distant edits in one call show as one block
    /// with the unchanged lines between them. Upgrade path: a real LCS diff if users complain.
    /// </summary>
    public static DysonFileChangeHunk? Compute(string? before, string after)
    {
        ArgumentNullException.ThrowIfNull(after);

        var addedLines = Split(after);
        if (before is null)
        {
            var createdText = Cap(string.Join('\n', addedLines), out var cut);
            return new DysonFileChangeHunk(1, null, null, createdText, null, cut);
        }

        var beforeLines = Split(before);
        var prefix = 0;
        var limit = Math.Min(beforeLines.Count, addedLines.Count);
        while (prefix < limit && beforeLines[prefix] == addedLines[prefix])
            prefix++;

        var beforeTail = beforeLines.Count;
        var afterTail = addedLines.Count;
        while (beforeTail > prefix && afterTail > prefix && beforeLines[beforeTail - 1] == addedLines[afterTail - 1])
        {
            beforeTail--;
            afterTail--;
        }

        if (beforeTail == prefix && afterTail == prefix)
            return null;

        var contextBefore = prefix > 0 ? Clip(beforeLines[prefix - 1]) : null;
        var contextAfter = beforeTail < beforeLines.Count ? Clip(beforeLines[beforeTail]) : null;
        var removed = Cap(beforeTail > prefix ? string.Join('\n', beforeLines.GetRange(prefix, beforeTail - prefix)) : null, out var removedCut);
        var added = Cap(afterTail > prefix ? string.Join('\n', addedLines.GetRange(prefix, afterTail - prefix)) : null, out var addedCut);
        return new DysonFileChangeHunk(prefix + 1, contextBefore, removed, added, contextAfter, removedCut || addedCut);
    }

    /// <summary>Display order: context, removed lines, added lines, context.</summary>
    public static IReadOnlyList<DysonFileChangeLine> ToLines(DysonFileChangeEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var lines = new List<DysonFileChangeLine>();
        Add(lines, DysonFileChangeLineKind.Context, row.ContextBefore);
        Add(lines, DysonFileChangeLineKind.Removed, row.RemovedText);
        Add(lines, DysonFileChangeLineKind.Added, row.AddedText);
        Add(lines, DysonFileChangeLineKind.Context, row.ContextAfter);
        return lines;
    }

    /// <summary>
    /// Secret-looking paths are still logged, but with the text replaced by <c>[content not logged]</c>.
    /// Matches the file name only: <c>.env*</c>, <c>*.pem</c>, <c>*.key</c>, <c>*.pfx</c>, <c>secrets.*</c>, <c>*.secrets.json</c>.
    /// </summary>
    public static bool ShouldRedact(string relativePath)
    {
        var name = Path.GetFileName(relativePath.Replace('\\', '/'));
        if (name.Length == 0)
            return false;

        return name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("secrets.", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".secrets.json", StringComparison.OrdinalIgnoreCase);
    }

    private static void Add(List<DysonFileChangeLine> lines, DysonFileChangeLineKind kind, string? text)
    {
        if (text is null)
            return;

        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n')
                continue;
            lines.Add(new DysonFileChangeLine(kind, text[start..i]));
            start = i + 1;
        }
    }

    private static List<string> Split(string text)
    {
        var normalized = DysonTextEditApplier.NormalizeLineEndings(text);
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string Clip(string line) =>
        line.Length <= ContextCap ? line : line[..ContextCap];

    private static string? Cap(string? text, out bool truncated)
    {
        if (text is null || text.Length <= TextCap)
        {
            truncated = false;
            return text;
        }

        var omitted = text.Length - TextCap;
        truncated = true;
        return text[..TextCap] + "\n… [truncated " + omitted.ToString(System.Globalization.CultureInfo.InvariantCulture) + " chars]";
    }
}
