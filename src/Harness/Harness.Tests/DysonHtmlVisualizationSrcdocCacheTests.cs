using DysonHarness;
using Harness.UI.Demo;

namespace Harness.Tests;

public class DysonHtmlVisualizationSrcdocCacheTests
{
    [Fact]
    public void SameRevision_ReturnsByteIdenticalSrcdoc_AndContentChangeBuildsANewOne()
    {
        var id = Guid.NewGuid();
        DysonHtmlVisualization Viz(string html) => new() { Id = id, Title = "Counter", Html = html, Css = "", JavaScript = "1;" };

        var cache = new DysonHtmlVisualizationSrcdocCache();
        Assert.True(cache.TryUpdate(Viz("<p>0</p>")));
        var first = cache.Document;

        // A second object with the same id and content (e.g. a restored copy) keeps the cached string.
        Assert.False(cache.TryUpdate(Viz("<p>0</p>")));
        Assert.Same(first, cache.Document);

        var changed = Viz("<p>1</p>");
        Assert.True(cache.TryUpdate(changed));
        Assert.NotEqual(DysonHtmlVisualizationIdentity.Capture(Viz("<p>0</p>")), DysonHtmlVisualizationIdentity.Capture(changed));
        Assert.NotEqual(first, cache.Document, StringComparer.Ordinal);
        Assert.Contains("<p>1</p>", cache.Document, StringComparison.Ordinal);
    }
}
