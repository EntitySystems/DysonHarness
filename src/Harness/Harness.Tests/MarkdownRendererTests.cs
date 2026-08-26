using Harness.UI.Markdown;

namespace Harness.Tests;

public class MarkdownRendererTests
{
    [Fact]
    public void ToHtml_highlights_csharp_fences_without_colorcode_wrapper()
    {
        var html = MarkdownRenderer.ToHtml("```csharp\npublic class Foo {}\n```").Value;

        Assert.Contains("language-csharp", html, StringComparison.Ordinal);
        Assert.True(
            html.Contains("class=\"keyword\"", StringComparison.Ordinal)
            || html.Contains("class=\"controlKeyword\"", StringComparison.Ordinal),
            html);
        Assert.DoesNotContain("<div class=\"csharp\">", html, StringComparison.Ordinal);
        Assert.Contains("<pre><code class=\"language-csharp\">", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("```go\npackage main\n```")]
    [InlineData("```\nplain fence\n```")]
    public void ToHtml_leaves_unknown_or_unlabeled_fences_as_escaped_code(string markdown)
    {
        var html = MarkdownRenderer.ToHtml(markdown).Value;

        Assert.Contains("<pre><code", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"keyword\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"controlKeyword\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ToHtml_does_not_emit_live_script_from_markdown_html()
    {
        var html = MarkdownRenderer.ToHtml("hello <script>alert(1)</script>").Value;

        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToHtml_escapes_html_inside_highlighted_fences()
    {
        var html = MarkdownRenderer.ToHtml("```csharp\n<img src=x onerror=alert(1)>\n```").Value;

        Assert.Contains("language-csharp", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;img", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToHtml_adds_noopener_noreferrer_on_http_links()
    {
        var html = MarkdownRenderer.ToHtml("[docs](https://example.com/x)").Value;

        Assert.Contains("rel=\"noopener noreferrer\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://example.com/x\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToHtml_returns_empty_markup_for_blank_input(string? markdown)
    {
        Assert.Equal("", MarkdownRenderer.ToHtml(markdown).Value);
    }

    [Fact]
    public void ToHtml_returns_same_value_instance_for_identical_source()
    {
        const string source = "hello **world**";
        var first = MarkdownRenderer.ToHtml(source);
        var second = MarkdownRenderer.ToHtml(source);
        var other = MarkdownRenderer.ToHtml("other markdown");

        Assert.Same(first.Value, second.Value);
        Assert.Contains("other markdown", other.Value, StringComparison.Ordinal);
        Assert.NotSame(first.Value, other.Value);
    }
}
