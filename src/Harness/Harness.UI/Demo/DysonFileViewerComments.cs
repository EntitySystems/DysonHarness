using System.Text;

using DysonHarness;

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
        if (DysonMetaPlanDisplayPath.TryParse(relativePath, out var planId))
        {
            sb.AppendLine().Append("Plan ").Append(planId)
                .AppendLine(" can be edited in place with EditMetaPlan (ReadMetaPlan first for the exact text). " +
                            "Apply small fixes yourself; relay substantive revisions to the authoring drone. Do not ask for a new plan.");
        }

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
