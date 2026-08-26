using System.Collections.Concurrent;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;

namespace Harness.UI.Markdown;

/// <summary>
/// Renders agent/user markdown for Blazor via <see cref="MarkupString"/>.
/// HTML input is disabled on the pipeline to avoid XSS from model output.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UsePipeTables()
        .UseTaskLists()
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml()
        .Use(new ColorCodeMarkdownExtension())
        .Build();

    private static readonly ConcurrentDictionary<string, MarkupString> HtmlCache = new();

    public static MarkupString ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return new MarkupString("");

        if (HtmlCache.TryGetValue(markdown, out var cached))
            return cached;

        var document = global::Markdig.Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (link.IsImage)
                continue;

            var url = link.Url;
            if (url is null
                || (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // Hardening only — clicks are intercepted in chat-external-links.js (do not rely on target=_blank).
            link.GetAttributes().AddPropertyIfNotExist("rel", "noopener noreferrer");
        }

        var html = new MarkupString(global::Markdig.Markdown.ToHtml(document, Pipeline));

        // ponytail: 64-entry ceiling; global Clear (not LRU). Upgrade to LRU if chat volume needs it.
        if (HtmlCache.Count >= 64)
            HtmlCache.Clear();

        HtmlCache[markdown] = html;
        return html;
    }
}
