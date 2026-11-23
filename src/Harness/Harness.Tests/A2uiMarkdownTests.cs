using Harness.UI.Components.Meta.A2ui;
using Xunit;

namespace Harness.Tests;

public sealed class A2uiMarkdownTests
{
    [Fact]
    public void RawHtml_comes_out_as_text()
    {
        var blocks = A2uiMarkdownBlocks.Parse("hello <b>bold</b> <script>alert(1)</script>\n\n<div onclick=\"x()\">block</div>");

        Assert.All(blocks, b => Assert.IsType<A2uiMdParagraph>(b));
        var first = Assert.IsType<A2uiMdParagraph>(blocks[0]);
        Assert.Contains("<b>bold</b>", A2uiMarkdownBlocks.PlainText(first.Inlines));
        Assert.Contains("<script>alert(1)</script>", A2uiMarkdownBlocks.PlainText(first.Inlines));
        var second = Assert.IsType<A2uiMdParagraph>(blocks[1]);
        Assert.Contains("<div onclick=\"x()\">", A2uiMarkdownBlocks.PlainText(second.Inlines));
    }

    [Fact]
    public void Link_is_label_plus_url_with_no_clickable_target()
    {
        var blocks = A2uiMarkdownBlocks.Parse("see [the docs](https://example.com/a?b=1) now");

        var paragraph = Assert.IsType<A2uiMdParagraph>(Assert.Single(blocks));
        var link = Assert.Single(paragraph.Inlines.OfType<A2uiMdLink>());
        Assert.Equal("the docs", A2uiMarkdownBlocks.PlainText(link.Content));
        Assert.Equal("https://example.com/a?b=1", link.Url);
        // The record carries data only: no handler, no href, nothing to invoke.
        Assert.DoesNotContain(typeof(A2uiMdLink).GetProperties(), p => p.PropertyType == typeof(Delegate) || p.PropertyType == typeof(Func<Task>));
    }

    [Fact]
    public void Image_becomes_alt_text()
    {
        var blocks = A2uiMarkdownBlocks.Parse("before ![a red dot](https://example.com/dot.png) after");

        var paragraph = Assert.IsType<A2uiMdParagraph>(Assert.Single(blocks));
        Assert.Empty(paragraph.Inlines.OfType<A2uiMdLink>());
        var text = A2uiMarkdownBlocks.PlainText(paragraph.Inlines);
        Assert.Equal("before a red dot after", text);
        Assert.DoesNotContain("example.com", text);
    }

    [Fact]
    public void Pipe_table_becomes_a_table_block()
    {
        var blocks = A2uiMarkdownBlocks.Parse("| Name | Count |\n| --- | --- |\n| a | 1 |\n| b | 2 |");

        var table = Assert.IsType<A2uiMdTable>(Assert.Single(blocks));
        Assert.Equal(3, table.Rows.Count);
        Assert.True(table.Rows[0].Header);
        Assert.Equal("Name", A2uiMarkdownBlocks.PlainText(table.Rows[0].Cells[0]));
        Assert.False(table.Rows[1].Header);
        Assert.Equal("2", A2uiMarkdownBlocks.PlainText(table.Rows[2].Cells[1]));
    }

    [Fact]
    public void Structure_and_emphasis_map_to_records()
    {
        var blocks = A2uiMarkdownBlocks.Parse("## Title\n\n**b** *i* ~~s~~ `c`\n\n- one\n- two\n\n```csharp\nvar x = 1;\n```");

        Assert.Equal(2, Assert.IsType<A2uiMdHeading>(blocks[0]).Level);
        var inlines = Assert.IsType<A2uiMdParagraph>(blocks[1]).Inlines;
        Assert.Contains(inlines, i => i is A2uiMdStyled { Style: A2uiMdStyle.Bold });
        Assert.Contains(inlines, i => i is A2uiMdStyled { Style: A2uiMdStyle.Italic });
        Assert.Contains(inlines, i => i is A2uiMdStyled { Style: A2uiMdStyle.Strike });
        Assert.Contains(inlines, i => i is A2uiMdCodeSpan { Text: "c" });
        Assert.Equal(2, Assert.IsType<A2uiMdList>(blocks[2]).Items.Count);
        var code = Assert.IsType<A2uiMdCode>(blocks[3]);
        Assert.Equal("csharp", code.Language);
        Assert.Contains("var x = 1;", code.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_input_is_no_blocks(string? markdown) => Assert.Empty(A2uiMarkdownBlocks.Parse(markdown));
}
