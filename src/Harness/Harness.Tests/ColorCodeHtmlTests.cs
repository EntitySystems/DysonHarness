using System.Reflection;
using Harness.UI.Markdown;

namespace Harness.Tests;

public class ColorCodeHtmlTests
{
    [Fact]
    public void TryHighlightSourceLines_highlights_csharp_without_the_colorcode_envelope()
    {
        var lines = TryHighlightSourceLines("dir/Example.cs", "using System;\npublic class Example { }");

        Assert.NotNull(lines);
        var html = lines!;
        Assert.Contains(html, line =>
            line.Contains("class=\"keyword\"", StringComparison.Ordinal)
            || line.Contains("class=\"controlKeyword\"", StringComparison.Ordinal));
        Assert.DoesNotContain(html, line => line.Contains("<div", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(html, line => line.Contains("<pre", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryHighlightSourceLines_carries_multiline_comment_span_to_the_next_line()
    {
        var lines = TryHighlightSourceLines("Example.cs", "/* comment\nnext */");

        Assert.NotNull(lines);

        Assert.Contains("class=\"comment\"", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("<span class=\"comment\">", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("</span>", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TryHighlightSourceLines_preserves_crlf_and_trailing_empty_source_line()
    {
        const string source = "var first = 1;\r\nvar second = 2;\r\n";

        var lines = TryHighlightSourceLines("Example.cs", source);

        Assert.NotNull(lines);
        Assert.Equal(source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Length, lines!.Length);
        Assert.Equal("", lines[^1]);
    }

    [Fact]
    public void TryHighlightSourceLines_encodes_hostile_source_markup()
    {
        var lines = TryHighlightSourceLines("Example.cs", "var text = \"<img src=x>&<script>\";");

        Assert.NotNull(lines);
        var html = string.Concat(lines!);

        Assert.Contains("&lt;img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&amp;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Example.go")]
    public void TryHighlightSourceLines_returns_null_for_blank_or_unknown_path(string? relativePath)
    {
        Assert.Null(TryHighlightSourceLines(relativePath, "package main"));
    }

    [Fact]
    public void TryHighlightSourceLines_returns_null_when_source_exceeds_limit()
    {
        Assert.Null(TryHighlightSourceLines("Example.cs", new string('x', 64 * 1024 + 1)));
    }

    [Fact]
    public void TryHighlightSourceLines_highlights_msbuild_project_files_as_xml()
    {
        Assert.Equal(25, ColorCodeLanguages.All.Count);

        const string source = "<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\n";
        var csproj = TryHighlightSourceLines("CashTrackServer.csproj", source);
        var fsproj = TryHighlightSourceLines("Library.fsproj", source);

        Assert.NotNull(csproj);
        Assert.NotNull(fsproj);
        Assert.Contains(csproj!, ContainsXmlClass);
        Assert.Contains(fsproj!, ContainsXmlClass);
    }

    private static bool ContainsXmlClass(string line) =>
        line.Contains("class=\"xmlElementName\"", StringComparison.Ordinal)
        || line.Contains("class=\"xmlTagDelimiter\"", StringComparison.Ordinal)
        || line.Contains("class=\"xmlName\"", StringComparison.Ordinal)
        || line.Contains("class=\"xmlDelimiter\"", StringComparison.Ordinal);

    private static string[]? TryHighlightSourceLines(string? relativePath, string content)
    {
        var helper = typeof(MarkdownRenderer).Assembly.GetType("Harness.UI.Markdown.ColorCodeHtml", throwOnError: true)!;
        var method = helper.GetMethod(
            "TryHighlightSourceLines",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        return (string[]?)method.Invoke(null, [relativePath, content]);
    }
}
