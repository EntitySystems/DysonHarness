using Harness.UI.Demo;

namespace Harness.Tests;

/// <summary>Plan comment excerpt / prompt formatting (Xunit).</summary>
public class DysonFileViewerCommentsTests
{
    [Fact]
    public void Run()
    {
        var longLine = new string('a', DysonFileViewerComments.ExcerptMaxLength + 20);
        var excerpt = DysonFileViewerComments.FormatExcerpt($"  {longLine}\nmore  ");
        if (excerpt.Length != DysonFileViewerComments.ExcerptMaxLength
            || excerpt.Contains('\n', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("FormatExcerpt must trim and cap at ExcerptMaxLength.");
        }

        var prompt = DysonFileViewerComments.FormatPrompt(
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
