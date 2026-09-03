using Microsoft.AspNetCore.Components;

namespace Harness.UI.Markdown;

/// <summary>
/// CPU-bound blank-line split + Markdig for the file viewer overlay.
/// Callers offload with <c>Task.Run</c>; this type does not.
/// </summary>
public static class DysonFileViewerMarkdown
{
    // ponytail: selection is paragraph/heading/pre blocks only (split on blank lines), not AST-precise; upgrade to Markdig block walker when comments persist.

    public static IReadOnlyList<DysonFileViewerMarkdownBlock> Build(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return [];

        var normalized = content.Replace("\r\n", "\n");
        var parts = normalized.Split("\n\n", StringSplitOptions.None);
        List<DysonFileViewerMarkdownBlock> blocks = [];
        var index = 0;
        var pos = 0;
        for (var p = 0; p < parts.Length; p++)
        {
            var part = parts[p];
            var startLine = SourceLineAt(normalized, pos);
            var endLine = part.Length == 0
                ? startLine
                : SourceLineAt(normalized, pos + part.Length - 1);
            pos += part.Length;
            if (p < parts.Length - 1)
                pos += 2;

            if (string.IsNullOrWhiteSpace(part))
                continue;

            blocks.Add(new DysonFileViewerMarkdownBlock(
                index++,
                part,
                MarkdownRenderer.ToHtml(part),
                startLine,
                endLine));
        }

        return blocks;
    }

    private static int SourceLineAt(string normalized, int position)
    {
        var line = 1;
        var limit = Math.Min(Math.Max(position, 0), normalized.Length);
        for (var i = 0; i < limit; i++)
        {
            if (normalized[i] == '\n')
                line++;
        }

        return line;
    }
}

public sealed record DysonFileViewerMarkdownBlock(
    int Index,
    string Source,
    MarkupString Html,
    int StartLine,
    int EndLine);
