using System.Text;

namespace Harness.UI.Demo;

/// <summary>Formats in-viewer plan comments into one Normal-turn prompt (UI-only until submit).</summary>
public static class DysonFileViewerComments
{
    public const int ExcerptMaxLength = 120;

    public static string FormatExcerpt(string blockOrLine)
    {
        var trimmed = blockOrLine.Trim().Replace("\r\n", "\n").Replace('\n', ' ');
        if (trimmed.Length <= ExcerptMaxLength)
            return trimmed;
        return trimmed[..ExcerptMaxLength];
    }

    public static string FormatPrompt(string relativePath, IEnumerable<(string Excerpt, string Text)> comments)
    {
        var sb = new StringBuilder();
        sb.Append("# Plan comments on `").Append(relativePath).AppendLine("`");
        var first = true;
        foreach (var (excerpt, text) in comments)
        {
            if (!first)
            {
                sb.AppendLine();
                sb.AppendLine("---");
            }

            first = false;
            sb.AppendLine();
            sb.Append("**On:** ").AppendLine(excerpt);
            sb.AppendLine();
            sb.AppendLine(text);
        }

        return sb.ToString();
    }

    /// <summary>ponytail: assert-only self-check (no test framework). Run from UI <c>Program</c>.</summary>
    public static void SelfCheck()
    {
        var longLine = new string('a', ExcerptMaxLength + 20);
        var excerpt = FormatExcerpt($"  {longLine}\nmore  ");
        if (excerpt.Length != ExcerptMaxLength || excerpt.Contains('\n', StringComparison.Ordinal))
            throw new InvalidOperationException("FormatExcerpt must trim and cap at ExcerptMaxLength.");

        var prompt = FormatPrompt(
            ".dyson/plans/demo.md",
            [("first block", "note one"), ("second", "note two")]);

        if (!prompt.StartsWith("# Plan comments on `.dyson/plans/demo.md`", StringComparison.Ordinal)
            || !prompt.Contains("**On:** first block", StringComparison.Ordinal)
            || !prompt.Contains("note one", StringComparison.Ordinal)
            || !prompt.Contains("---", StringComparison.Ordinal)
            || !prompt.Contains("**On:** second", StringComparison.Ordinal)
            || !prompt.Contains("note two", StringComparison.Ordinal)
            || prompt.Contains("## On:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("FormatPrompt shape mismatch.");
        }
    }
}
