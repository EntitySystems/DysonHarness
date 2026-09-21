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

        // ponytail: 256-entry ceiling, insertion-order eviction (not true LRU — a cache hit does not
        // bump Seq, so a hot entry can still be evicted before a cold one). Overflow does an O(n) scan
        // over the ~256 entries to find the oldest ~64 by Seq instead of Clear()-ing everything, so a
        // long transcript scroll degrades gradually instead of cold-starting every turn's HTML at once.
        // Upgrade to a real LRU (bump Seq on read) if eviction-of-hot-entries becomes visible.
        // The ceiling was 64 while collapsed turns were @if-removed from the render tree. TurnBlock
        // now keeps every turn's body mounted, so one render pass touches every turn's instruction,
        // reply and reasoning bodies — at ~13 renders/sec a 64-entry cache would thrash and re-run
        // Markdig + ColorCode for the whole transcript on every delta.
        if (HtmlCache.Count >= 256)
        {
            var oldest = HtmlCache.OrderBy(static entry => entry.Value.Seq).Take(64);
            foreach (var entry in oldest)
                HtmlCache.TryRemove(entry.Key, out _);
        }

        HtmlCache[markdown] = (Interlocked.Increment(ref _cacheSeq), html);
        return html;
    }
}
