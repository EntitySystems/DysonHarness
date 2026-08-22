using ColorCode;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Harness.UI.Markdown;

internal sealed class ColorCodeMarkdownExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is not HtmlRenderer html)
            return;

        html.ObjectRenderers.ReplaceOrAdd<CodeBlockRenderer>(new ColorCodeCodeBlockRenderer());
    }
}

internal sealed class ColorCodeCodeBlockRenderer : CodeBlockRenderer
{
    protected override void Write(HtmlRenderer renderer, CodeBlock obj)
    {
        if (!TryWriteHighlighted(renderer, obj))
            base.Write(renderer, obj);
    }

    private static bool TryWriteHighlighted(HtmlRenderer renderer, CodeBlock block)
    {
        if (block is not FencedCodeBlock fenced)
            return false;

        var language = ColorCodeLanguages.TryResolve(fenced.Info);
        if (language is null)
            return false;

        var source = fenced.Lines.ToString();
        // ponytail: regex highlighter CPU/HTML size; raise or skip per-block if chat fences get huge.
        var formatted = ColorCodeHtml.TryFormat(source, language);
        if (formatted is null || !ColorCodeHtml.TryUnwrap(formatted, out var inner))
            return false;

        renderer.EnsureLine();
        renderer.Write("<pre><code class=\"language-");
        renderer.Write(language.CssClassName);
        renderer.Write("\">");
        renderer.Write(inner);
        renderer.WriteLine("</code></pre>");
        return true;
    }

}
