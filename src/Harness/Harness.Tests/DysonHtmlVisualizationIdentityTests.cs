using DysonHarness;

namespace Harness.Tests;

public class DysonHtmlVisualizationIdentityTests
{
    private static readonly Guid Id = Guid.NewGuid();

    private static DysonHtmlVisualization Viz(
        string title = "t", string html = "<p>h</p>", string css = "p{}", string js = "1;", Guid? id = null) => new()
    {
        Id = id ?? Id,
        Title = title,
        Html = html,
        Css = css,
        JavaScript = js,
    };

    [Fact]
    public void SameContent_SameHash_NoUpdate()
    {
        var applied = DysonHtmlVisualizationIdentity.Capture(Viz());

        Assert.Equal(applied, DysonHtmlVisualizationIdentity.Capture(Viz()));
        Assert.Matches("^[0-9a-f]{64}$", applied.ContentHash);
        Assert.False(DysonHtmlVisualizationIdentity.NeedsUpdate(applied, Viz()));
    }

    [Fact]
    public void AnyFieldOrIdChange_NeedsUpdate()
    {
        var applied = DysonHtmlVisualizationIdentity.Capture(Viz());

        Assert.True(DysonHtmlVisualizationIdentity.NeedsUpdate(null, Viz()));
        Assert.True(DysonHtmlVisualizationIdentity.NeedsUpdate(applied, Viz(title: "t2")));
        Assert.True(DysonHtmlVisualizationIdentity.NeedsUpdate(applied, Viz(html: "<p>x</p>")));
        Assert.True(DysonHtmlVisualizationIdentity.NeedsUpdate(applied, Viz(css: "p{color:red}")));
        Assert.True(DysonHtmlVisualizationIdentity.NeedsUpdate(applied, Viz(js: "2;")));
        Assert.True(DysonHtmlVisualizationIdentity.NeedsUpdate(applied, Viz(id: Guid.NewGuid())));
    }

    [Fact]
    public void FieldsAreLengthPrefixed()
    {
        Assert.NotEqual(
            DysonHtmlVisualizationIdentity.Capture(Viz(title: "ab", html: "c")).ContentHash,
            DysonHtmlVisualizationIdentity.Capture(Viz(title: "a", html: "bc")).ContentHash);
    }
}
