using Markdig;
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
        .Build();

    public static MarkupString ToHtml(string? markdown) =>
        string.IsNullOrWhiteSpace(markdown)
            ? new MarkupString("")
            : new MarkupString(global::Markdig.Markdown.ToHtml(markdown, Pipeline));
}
