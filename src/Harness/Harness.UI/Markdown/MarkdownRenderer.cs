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

    private static readonly ConcurrentDictionary<string, (long Seq, MarkupString Html)> HtmlCache = new();
    private static long _cacheSeq;

    public static MarkupString ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return new MarkupString("");

        if (HtmlCache.TryGetValue(markdown, out var cachedEntry))
            return cachedEntry.Html;

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

        // ponytail: 64-entry ceiling, insertion-order eviction (not true LRU — a cache hit does not
        // bump Seq, so a hot entry can still be evicted before a cold one). Overflow does an O(n) scan
        // over the ~64 entries to find the oldest ~16 by Seq instead of Clear()-ing everything, so a
        // long transcript scroll degrades gradually instead of cold-starting every turn's HTML at once.
        // Upgrade to a real LRU (bump Seq on read) if eviction-of-hot-entries becomes visible.
        if (HtmlCache.Count >= 64)
        {
            var oldest = HtmlCache.OrderBy(static entry => entry.Value.Seq).Take(16);
            foreach (var entry in oldest)
                HtmlCache.TryRemove(entry.Key, out _);
        }

        HtmlCache[markdown] = (Interlocked.Increment(ref _cacheSeq), html);
        return html;
    }
}
