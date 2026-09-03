using Harness.UI.Markdown;

namespace Harness.Tests;

/// <summary>Blank-line markdown split + Markdig ToHtml for the file viewer (Xunit).</summary>
public class DysonFileViewerMarkdownTests
{
    [Fact]
    public void Run()
    {
        var two = DysonFileViewerMarkdown.Build("hello\n\nworld");
        if (two.Count != 2
            || two[0].Index != 0
            || two[1].Index != 1
            || two[0].Source != "hello"
            || two[1].Source != "world"
            || two[0].StartLine != 1
            || two[0].EndLine != 1
            || two[1].StartLine != 3
            || two[1].EndLine != 3
            || !two[0].Html.Value.Contains("<p>", StringComparison.Ordinal)
            || !two[1].Html.Value.Contains("<p>", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Two paragraphs must yield Index 0/1, StartLine/EndLine 1 and 3, matching Source, and <p> Html.");
        }

        if (DysonFileViewerMarkdown.Build(null).Count != 0
            || DysonFileViewerMarkdown.Build("").Count != 0
            || DysonFileViewerMarkdown.Build("   \n\n\t").Count != 0)
        {
            throw new InvalidOperationException("Empty / whitespace-only content must yield an empty list.");
        }

        var crlf = DysonFileViewerMarkdown.Build("alpha\r\n\r\nbeta");
        if (crlf.Count != 2 || crlf[0].Source != "alpha" || crlf[1].Source != "beta")
            throw new InvalidOperationException("CRLF blank-line split must match LF.");

        var consecutive = DysonFileViewerMarkdown.Build("one\n\n\n\ntwo");
        if (consecutive.Count != 2 || consecutive[0].Source != "one" || consecutive[1].Source != "two")
            throw new InvalidOperationException("Consecutive blank lines must not emit empty blocks.");

        var heading = DysonFileViewerMarkdown.Build("# Title\n\nbody");
        if (heading.Count != 2
            || heading[0].Index != 0
            || heading[1].Index != 1
            || heading[0].Source != "# Title"
            || heading[1].Source != "body"
            || !heading[0].Html.Value.Contains("<h1>", StringComparison.Ordinal)
            || !heading[1].Html.Value.Contains("<p>", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Heading + paragraph must yield two blocks with h1 and p Html.");
        }
    }
}
