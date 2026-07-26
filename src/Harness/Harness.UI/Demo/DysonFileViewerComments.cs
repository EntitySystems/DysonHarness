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
}
