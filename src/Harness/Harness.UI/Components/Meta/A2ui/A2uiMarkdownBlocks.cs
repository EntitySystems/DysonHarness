using System.Globalization;
using System.Text;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Harness.UI.Components.Meta.A2ui;

// Plain block/inline records for the A2UI Markdown component. The Markdig AST is walked into these and
// A2uiMarkdown.razor emits ordinary Blazor elements from them: no HTML string, no MarkupString.

public abstract record A2uiMdBlock;

public sealed record A2uiMdParagraph(IReadOnlyList<A2uiMdInline> Inlines) : A2uiMdBlock;

public sealed record A2uiMdHeading(int Level, IReadOnlyList<A2uiMdInline> Inlines) : A2uiMdBlock;

public sealed record A2uiMdCode(string Text, string? Language) : A2uiMdBlock;

public sealed record A2uiMdList(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<A2uiMdBlock>> Items) : A2uiMdBlock;

public sealed record A2uiMdQuote(IReadOnlyList<A2uiMdBlock> Blocks) : A2uiMdBlock;

public sealed record A2uiMdTable(IReadOnlyList<A2uiMdRow> Rows) : A2uiMdBlock;

public sealed record A2uiMdRule : A2uiMdBlock;

public sealed record A2uiMdRow(bool Header, IReadOnlyList<IReadOnlyList<A2uiMdInline>> Cells);

public abstract record A2uiMdInline;

public sealed record A2uiMdText(string Text) : A2uiMdInline;

public enum A2uiMdStyle
{
    Bold,
    Italic,
    Strike,
}

public sealed record A2uiMdStyled(A2uiMdStyle Style, IReadOnlyList<A2uiMdInline> Children) : A2uiMdInline;

public sealed record A2uiMdCodeSpan(string Text) : A2uiMdInline;

public sealed record A2uiMdBreak : A2uiMdInline;

/// <summary>A link: label inlines plus its URL. Never clickable; the renderer shows the URL as muted text.</summary>
public sealed record A2uiMdLink(IReadOnlyList<A2uiMdInline> Content, string Url) : A2uiMdInline;

/// <summary>Markdig AST → <see cref="A2uiMdBlock"/> records. Pure; never throws on odd input.</summary>
public static class A2uiMarkdownBlocks
{
    public const int MaxChars = 20_000;
    private const int MaxDepth = 6;
    private const int MaxNodes = 2_000;
    private const int MaxTableRows = 100;
    private const int MaxLanguageLength = 32;

    // DisableHtml: raw HTML is never parsed as HTML, it stays literal text.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UsePipeTables()
        .UseTaskLists()
        .DisableHtml()
        .Build();

    public static IReadOnlyList<A2uiMdBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        var text = markdown.Length > MaxChars ? markdown[..MaxChars] : markdown;
        var document = global::Markdig.Markdown.Parse(text, Pipeline);
        return new Walker().Blocks(document, 0);
    }

    /// <summary>Visible text of inlines (links as their label only, images as alt text).</summary>
    public static string PlainText(IReadOnlyList<A2uiMdInline> inlines)
    {
        var sb = new StringBuilder();
        Append(sb, inlines);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, IReadOnlyList<A2uiMdInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case A2uiMdText t:
                    sb.Append(t.Text);
                    break;
                case A2uiMdCodeSpan c:
                    sb.Append(c.Text);
                    break;
                case A2uiMdBreak:
                    sb.Append(' ');
                    break;
                case A2uiMdStyled s:
                    Append(sb, s.Children);
                    break;
                case A2uiMdLink l:
                    Append(sb, l.Content);
                    break;
            }
        }
    }

    private sealed class Walker
    {
        private int _budget = MaxNodes;

        public List<A2uiMdBlock> Blocks(ContainerBlock container, int depth)
        {
            var result = new List<A2uiMdBlock>();
            if (depth > MaxDepth)
                return result;

            foreach (var block in container)
            {
                if (_budget-- <= 0)
                    break;

                switch (block)
                {
                    case HeadingBlock h:
                        result.Add(new A2uiMdHeading(Math.Clamp(h.Level, 1, 6), Inlines(h.Inline, depth)));
                        break;
                    case ParagraphBlock p:
                        result.Add(new A2uiMdParagraph(Inlines(p.Inline, depth)));
                        break;
                    case FencedCodeBlock f:
                        result.Add(new A2uiMdCode(f.Lines.ToString(), Language(f.Info)));
                        break;
                    case CodeBlock c:
                        result.Add(new A2uiMdCode(c.Lines.ToString(), null));
                        break;
                    case QuoteBlock q:
                        result.Add(new A2uiMdQuote(Blocks(q, depth + 1)));
                        break;
                    case ListBlock l:
                        result.Add(ListOf(l, depth));
                        break;
                    case Table t:
                        result.Add(TableBlock(t, depth));
                        break;
                    case ThematicBreakBlock:
                        result.Add(new A2uiMdRule());
                        break;
                    case HtmlBlock h:
                        result.Add(new A2uiMdParagraph([new A2uiMdText(h.Lines.ToString())]));
                        break;
                    case LeafBlock { Inline: { } inline }:
                        result.Add(new A2uiMdParagraph(Inlines(inline, depth)));
                        break;
                    case ContainerBlock inner:
                        result.AddRange(Blocks(inner, depth + 1));
                        break;
                }
            }

            return result;
        }

        private A2uiMdList ListOf(ListBlock list, int depth)
        {
            var items = new List<IReadOnlyList<A2uiMdBlock>>();
            foreach (var child in list)
            {
                if (child is ListItemBlock item && items.Count < MaxTableRows)
                    items.Add(Blocks(item, depth + 1));
            }

            var start = int.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n < 100_000 ? n : 1;
            return new A2uiMdList(list.IsOrdered, start, items);
        }

        private A2uiMdTable TableBlock(Table table, int depth)
        {
            var rows = new List<A2uiMdRow>();
            foreach (var child in table)
            {
                if (child is not TableRow row || rows.Count >= MaxTableRows)
                    continue;

                var cells = new List<IReadOnlyList<A2uiMdInline>>();
                foreach (var cellBlock in row)
                {
                    if (cellBlock is not TableCell cell)
                        continue;
                    var inlines = new List<A2uiMdInline>();
                    foreach (var part in cell)
                    {
                        if (part is LeafBlock { Inline: { } inline })
                            inlines.AddRange(Inlines(inline, depth));
                    }

                    cells.Add(inlines);
                }

                rows.Add(new A2uiMdRow(row.IsHeader, cells));
            }

            return new A2uiMdTable(rows);
        }

        public List<A2uiMdInline> Inlines(ContainerInline? container, int depth)
        {
            var result = new List<A2uiMdInline>();
            if (container is null || depth > MaxDepth * 2)
                return result;

            foreach (var inline in container)
            {
                if (_budget-- <= 0)
                    break;

                switch (inline)
                {
                    case LiteralInline l:
                        result.Add(new A2uiMdText(l.Content.ToString()));
                        break;
                    case CodeInline c:
                        result.Add(new A2uiMdCodeSpan(c.Content));
                        break;
                    case LineBreakInline:
                        result.Add(new A2uiMdBreak());
                        break;
                    case HtmlEntityInline e:
                        result.Add(new A2uiMdText(e.Transcoded.ToString()));
                        break;
                    case HtmlInline h:
                        result.Add(new A2uiMdText(h.Tag));
                        break;
                    case AutolinkInline a:
                        result.Add(new A2uiMdLink([new A2uiMdText(a.Url)], a.Url));
                        break;
                    case TaskList task:
                        result.Add(new A2uiMdText(task.Checked ? "[x] " : "[ ] "));
                        break;
                    case LinkInline link when link.IsImage:
                        var alt = PlainText(Inlines(link, depth + 1));
                        result.Add(new A2uiMdText(alt.Length > 0 ? alt : "image"));
                        break;
                    case LinkInline link:
                        result.Add(new A2uiMdLink(Inlines(link, depth + 1), link.Url ?? ""));
                        break;
                    case EmphasisInline em:
                        Emphasis(result, em, depth);
                        break;
                    case ContainerInline inner:
                        result.AddRange(Inlines(inner, depth + 1));
                        break;
                }
            }

            return result;
        }

        private void Emphasis(List<A2uiMdInline> into, EmphasisInline em, int depth)
        {
            var children = Inlines(em, depth + 1);
            switch (em.DelimiterChar, (int)em.DelimiterCount)
            {
                case ('*' or '_', 1):
                    into.Add(new A2uiMdStyled(A2uiMdStyle.Italic, children));
                    break;
                case ('*' or '_', 2):
                    into.Add(new A2uiMdStyled(A2uiMdStyle.Bold, children));
                    break;
                case ('*' or '_', >= 3):
                    into.Add(new A2uiMdStyled(A2uiMdStyle.Bold, [new A2uiMdStyled(A2uiMdStyle.Italic, children)]));
                    break;
                case ('~', 2):
                    into.Add(new A2uiMdStyled(A2uiMdStyle.Strike, children));
                    break;
                default: // ~sub~, ^sup^, ++ins++, ==mark==: shown as plain text
                    into.AddRange(children);
                    break;
            }
        }

        private static string? Language(string? info)
        {
            var language = info?.Trim();
            if (string.IsNullOrEmpty(language))
                return null;
            return language.Length > MaxLanguageLength ? language[..MaxLanguageLength] : language;
        }
    }
}
