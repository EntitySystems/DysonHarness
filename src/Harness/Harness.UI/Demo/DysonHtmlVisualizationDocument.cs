using System.Net;
using System.Security.Cryptography;
using System.Text;
using DysonHarness;

namespace Harness.UI.Demo;

/// <summary>Builds the isolated document used by HTML visualization iframes.</summary>
public static class DysonHtmlVisualizationDocument
{
    /// <summary>Returns a complete, CSP-constrained iframe <c>srcdoc</c> document.</summary>
    public static string Create(DysonHtmlVisualization visualization)
    {
        ArgumentNullException.ThrowIfNull(visualization);

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var title = WebUtility.HtmlEncode(visualization.Title ?? string.Empty);
        var css = Convert.ToBase64String(Encoding.UTF8.GetBytes(visualization.Css ?? string.Empty));
        var javaScript = Convert.ToBase64String(Encoding.UTF8.GetBytes(visualization.JavaScript ?? string.Empty));
        var csp = $"default-src 'none'; style-src 'nonce-{nonce}'; script-src 'nonce-{nonce}' blob:; img-src data: blob:; font-src data:; media-src data: blob:; connect-src 'none'; object-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'";

        var document = new StringBuilder();
        document.AppendLine("<!doctype html>");
        document.AppendLine("<html lang=\"en\">");
        document.AppendLine("<head>");
        document.AppendLine("<meta charset=\"utf-8\">");
        document.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(csp).AppendLine("\">");
        document.AppendLine("<meta name=\"referrer\" content=\"no-referrer\">");
        document.Append("<title>").Append(title).AppendLine("</title>");
        document.AppendLine("</head>");
        document.AppendLine("<body>");
        document.AppendLine(visualization.Html ?? string.Empty);
        document.Append("<script nonce=\"").Append(nonce).AppendLine("\">");
        document.AppendLine("(() => {");
        document.AppendLine("const decode = value => new TextDecoder().decode(Uint8Array.from(atob(value), c => c.charCodeAt(0)));");
        document.AppendLine("const style = document.createElement('style');");
        document.Append("style.nonce = '").Append(nonce).AppendLine("';");
        document.Append("style.textContent = decode('").Append(css).AppendLine("');");
        document.AppendLine("document.head.append(style);");
        document.Append("const url = URL.createObjectURL(new Blob([decode('").Append(javaScript)
            .AppendLine("')], { type: 'text/javascript' }));");
        document.AppendLine("const script = document.createElement('script');");
        document.AppendLine("script.src = url;");
        document.AppendLine("script.addEventListener('load', () => URL.revokeObjectURL(url), { once: true });");
        document.AppendLine("script.addEventListener('error', () => URL.revokeObjectURL(url), { once: true });");
        document.AppendLine("document.body.append(script);");
        document.AppendLine("})();");
        document.AppendLine("</script>");
        document.AppendLine("</body>");
        document.AppendLine("</html>");
        return document.ToString();
    }
}
