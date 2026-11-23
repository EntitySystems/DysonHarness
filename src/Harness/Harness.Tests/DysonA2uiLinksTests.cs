using DysonHarness;

namespace Harness.Tests;

/// <summary>LinkTarget / MediaUrl normalisation and host extraction.</summary>
public class DysonA2uiLinksTests
{
    [Fact]
    public void Https_becomes_open_url_and_builtin_keys_pass_unchanged()
    {
        Assert.Equal("open_url:https://example.com/a?b=1", DysonA2uiLinks.TryLinkFunc("https://example.com/a?b=1").Value);
        Assert.Equal("open_url:http://example.com", DysonA2uiLinks.TryLinkFunc("http://example.com").Value);
        Assert.Equal("open_file:docs/a.md", DysonA2uiLinks.TryLinkFunc("open_file:docs/a.md").Value);
        Assert.Equal("open_plan:364", DysonA2uiLinks.TryLinkFunc("open_plan:364").Value);
        Assert.Equal("open_scratch:7", DysonA2uiLinks.TryLinkFunc("open_scratch:7").Value);
        Assert.Equal("open_url:https://example.com", DysonA2uiLinks.TryLinkFunc("open_url:https://example.com").Value);
    }

    [Theory]
    [InlineData("mailto:a@b.c")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,AAAA")]
    [InlineData("/relative/path")]
    [InlineData("relative.html")]
    [InlineData("ftp://example.com/x")]
    [InlineData("https://exa mple.com")]
    [InlineData("")]
    [InlineData(null)]
    public void Unsafe_or_relative_links_are_rejected(string? raw)
    {
        var result = DysonA2uiLinks.TryLinkFunc(raw);
        Assert.True(result.IsError);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Overlong_link_is_rejected()
    {
        Assert.True(DysonA2uiLinks.TryLinkFunc("https://example.com/" + new string('a', DysonA2uiProtocol.MaxMediaUrlLength)).IsError);
    }

    [Fact]
    public void MediaUrl_is_http_only_and_bounded()
    {
        Assert.True(DysonA2uiLinks.IsMediaUrl("https://example.com/a.png"));
        Assert.False(DysonA2uiLinks.IsMediaUrl("data:image/png;base64,AAAA"));
        Assert.False(DysonA2uiLinks.IsMediaUrl("open_file:a.png"));
        Assert.False(DysonA2uiLinks.IsMediaUrl(null));
        Assert.False(DysonA2uiLinks.IsMediaUrl("https://example.com/" + new string('a', DysonA2uiProtocol.MaxMediaUrlLength)));
    }

    [Fact]
    public void Host_is_lowercase_without_www_and_null_when_invalid()
    {
        Assert.Equal("example.com", DysonA2uiLinks.Host("https://www.Example.com/x"));
        Assert.Equal("docs.example.org", DysonA2uiLinks.Host("open_url:https://docs.example.org:8443/a"));
        Assert.Null(DysonA2uiLinks.Host("not a url"));
        Assert.Null(DysonA2uiLinks.Host("open_plan:3"));
        Assert.Null(DysonA2uiLinks.Host(null));
    }
}
