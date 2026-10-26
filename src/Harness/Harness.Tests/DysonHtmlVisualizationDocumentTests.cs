using System.Text.RegularExpressions;
using DysonHarness;
using Harness.UI.Demo;

namespace Harness.Tests;

public class DysonHtmlVisualizationDocumentTests
{
    [Fact]
    public void Create_EncodesWrapperSourcesAndConstrainsContent()
    {
        var document = DysonHtmlVisualizationDocument.Create(new DysonHtmlVisualization
        {
            Id = Guid.NewGuid(),
            Title = "<Quarterly & revenue>",
            Html = "<main id=\"chart\">chart</main>",
            Css = "body::after { content: '</style><script>bad</script>'; }",
            JavaScript = "// </script>\ndocument.body.dataset.ready = 'true';",
        });

        if (!document.Contains("default-src 'none'", StringComparison.Ordinal)
            || !document.Contains("connect-src 'none'", StringComparison.Ordinal)
            || document.Contains("allow-same-origin", StringComparison.Ordinal)
            || !document.Contains("&lt;Quarterly &amp; revenue&gt;", StringComparison.Ordinal)
            || !document.Contains("URL.createObjectURL", StringComparison.Ordinal)
            || !document.Contains("URL.revokeObjectURL", StringComparison.Ordinal)
            || document.Contains("new Function", StringComparison.Ordinal)
            || document.Contains("eval(", StringComparison.Ordinal)
            || document.Contains("</style><script>bad</script>", StringComparison.Ordinal)
            || document.Contains("// </script>", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Visualization document isolation wrapper mismatch.");
        }
    }

    [Fact]
    public void Create_EmitsTheSameNonceInCspAndInScriptAndStyle()
    {
        var document = DysonHtmlVisualizationDocument.Create(new DysonHtmlVisualization
        {
            Id = Guid.NewGuid(),
            Title = "Nonce",
            Html = "<p>n</p>",
            Css = "p{}",
            JavaScript = "1;",
        });

        var nonce = Regex.Match(document, "script-src 'nonce-([0-9a-f]{32})'").Groups[1].Value;
        Assert.NotEmpty(nonce);
        Assert.Contains($"style-src 'nonce-{nonce}'", document, StringComparison.Ordinal);
        Assert.Contains($"<script nonce=\"{nonce}\">", document, StringComparison.Ordinal);
        Assert.Contains($"style.nonce = '{nonce}';", document, StringComparison.Ordinal);
        Assert.All(Regex.Matches(document, "[0-9a-f]{32}").Select(match => match.Value), value => Assert.Equal(nonce, value));
    }
}
