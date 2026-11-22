using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>A table cell as shown (<see cref="Text"/>) and as sorted (<see cref="Number"/>, null when not numeric).</summary>
public readonly record struct DysonA2uiCell(string Text, double? Number)
{
    /// <summary>The empty cell. Use this, never <c>default</c>, whose Text is null.</summary>
    public static DysonA2uiCell Empty { get; } = new("", null);
}

/// <summary>One page of a filtered, sorted table: row indexes into the source rows (<see cref="Rows"/>), 0-based <see cref="Page"/>.</summary>
public sealed record DysonA2uiTablePage(IReadOnlyList<int> Rows, int Matches, int Page, int PageCount, int PageSize);

/// <summary>Half-step star state for <see cref="DysonA2uiTable.Stars"/>.</summary>
public enum DysonA2uiStar { Empty, Half, Full }

/// <summary>
/// Pure table, avatar and gauge helpers for the A2UI data display group. Everything here tolerates
/// bound (unvalidated) data: nothing throws, odd input is clamped or skipped. O(n log n) over at most
/// <see cref="DysonA2uiProtocol.MaxTableRows"/> rows, computed synchronously.
/// </summary>
public static class DysonA2uiTable
{
    private const int ColorCount = 6;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// JSON value → cell. Numbers are formatted with <paramref name="format"/> (number/currency/percent/compact;
    /// unknown → number); a numeric string keeps its text but sorts as a number; <c>date</c> shows yyyy-MM-dd;
    /// booleans show Yes/No; null, objects and arrays are empty.
    /// </summary>
    public static DysonA2uiCell Display(JsonNode? node, string? format)
    {
        if (node is not JsonValue v)
            return DysonA2uiCell.Empty;
        switch (v.GetValueKind())
        {
            case JsonValueKind.String:
                var s = v.GetValue<string>();
                if (format == "date")
                    return new(DysonA2uiFormat.Date(s), null);
                var parsed = ParseNumber(s);
                return format is "number" or "currency" or "percent" or "compact" && parsed is { } n
                    ? new(DysonA2uiFormat.Number(n, format), n)
                    : new(s, parsed);
            case JsonValueKind.Number when v.TryGetValue<double>(out var d) && double.IsFinite(d):
                return new(DysonA2uiFormat.Number(d, format), d);
            case JsonValueKind.True:
                return new("Yes", null);
            case JsonValueKind.False:
                return new("No", null);
            default:
                return DysonA2uiCell.Empty;
        }
    }

    /// <summary>
    /// Stable permutation of column positions. Numeric when every non-empty cell has a number, else
    /// ordinal-ignore-case text. Empty cells sort last in both directions; ties keep source order.
    /// </summary>
    public static int[] Sort(IReadOnlyList<DysonA2uiCell> column, bool descending)
    {
        var order = new int[column.Count];
        for (var i = 0; i < order.Length; i++)
            order[i] = i;

        var numeric = false;
        var text = false;
        foreach (var cell in column)
        {
            if (cell.Number is not null)
                numeric = true;
            else if (!string.IsNullOrEmpty(cell.Text))
                text = true;
        }

        var byNumber = numeric && !text;
        Array.Sort(order, (a, b) =>
        {
            var x = column[a];
            var y = column[b];
            var xEmpty = x.Number is null && string.IsNullOrEmpty(x.Text);
            var yEmpty = y.Number is null && string.IsNullOrEmpty(y.Text);
            if (xEmpty || yEmpty)
                return xEmpty && yEmpty ? a.CompareTo(b) : xEmpty ? 1 : -1;
            var c = byNumber
                ? x.Number!.Value.CompareTo(y.Number!.Value)
                : string.Compare(x.Text ?? "", y.Text ?? "", StringComparison.OrdinalIgnoreCase);
            if (descending)
                c = -c;
            return c != 0 ? c : a.CompareTo(b);
        });
        return order;
    }

    /// <summary>Indexes of rows where any cell contains <paramref name="query"/> (ordinal, ignore case). Blank query keeps all.</summary>
    public static int[] Filter(IReadOnlyList<IReadOnlyList<DysonA2uiCell>> rows, string? query)
    {
        var needle = query?.Trim() ?? "";
        var kept = new List<int>(rows.Count);
        for (var r = 0; r < rows.Count; r++)
        {
            if (needle.Length == 0)
            {
                kept.Add(r);
                continue;
            }

            foreach (var cell in rows[r])
            {
                if (cell.Text?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true)
                {
                    kept.Add(r);
                    break;
                }
            }
        }

        return [.. kept];
    }

    /// <summary>
    /// Filter, then sort by <paramref name="sortColumn"/> (out of range = source order), then slice one page.
    /// <paramref name="pageSize"/> ≤ 0 shows everything on one page; <paramref name="page"/> is clamped.
    /// </summary>
    public static DysonA2uiTablePage Query(
        IReadOnlyList<IReadOnlyList<DysonA2uiCell>> rows, string? query, int sortColumn, bool descending, int page, int pageSize)
    {
        var matches = Filter(rows, query);
        if (sortColumn >= 0 && matches.Length > 1)
        {
            var column = new DysonA2uiCell[matches.Length];
            for (var i = 0; i < matches.Length; i++)
            {
                var row = rows[matches[i]];
                column[i] = sortColumn < row.Count ? row[sortColumn] : DysonA2uiCell.Empty;
            }

            var order = Sort(column, descending);
            var sorted = new int[matches.Length];
            for (var i = 0; i < order.Length; i++)
                sorted[i] = matches[order[i]];
            matches = sorted;
        }

        var size = pageSize <= 0 ? Math.Max(1, matches.Length) : pageSize;
        var pageCount = Math.Max(1, (int)Math.Ceiling(matches.Length / (double)size));
        var current = Math.Clamp(page, 0, pageCount - 1);
        var start = Math.Min(current * size, matches.Length);
        var count = Math.Min(size, matches.Length - start);
        return new DysonA2uiTablePage(matches.AsSpan(start, count).ToArray(), matches.Length, current, pageCount, pageSize <= 0 ? 0 : size);
    }

    /// <summary>
    /// Avatar initials: first letter of the first two words, upper-cased (invariant). Non-Latin text works
    /// (grapheme based). One word gives one letter; no letter or digit at all gives "?".
    /// </summary>
    public static string Initials(string? name)
    {
        var letters = new List<string>(2);
        foreach (var word in (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (FirstLetter(word) is { } letter)
                letters.Add(letter);
            if (letters.Count == 2)
                break;
        }

        return letters.Count == 0 ? "?" : string.Concat(letters).ToUpperInvariant();
    }

    /// <summary>Stable palette index 0..5 for a name (FNV-1a over the trimmed, lower-cased text). Same name, same colour.</summary>
    public static int ColorIndex(string? name)
    {
        var hash = 2166136261u;
        foreach (var ch in (name ?? "").Trim().ToLowerInvariant())
            hash = unchecked((hash ^ ch) * 16777619u);
        return (int)(hash % ColorCount);
    }

    /// <summary>Clamps to [min, max]; NaN gives min.</summary>
    public static double Clamp(double value, double min, double max) =>
        double.IsNaN(value) ? min : Math.Min(max, Math.Max(min, value));

    /// <summary>Five stars for a 0..5 rating, rounded to halves; out of range is clamped.</summary>
    public static DysonA2uiStar[] Stars(double value)
    {
        var halves = (int)Math.Round(Clamp(value, 0, 5) * 2, MidpointRounding.AwayFromZero);
        var stars = new DysonA2uiStar[5];
        for (var i = 0; i < stars.Length; i++)
            stars[i] = halves >= (i + 1) * 2 ? DysonA2uiStar.Full : halves == i * 2 + 1 ? DysonA2uiStar.Half : DysonA2uiStar.Empty;
        return stars;
    }

    /// <summary>SVG <c>stroke-dasharray</c> ("arc circumference") for a ring of <paramref name="radius"/> filled to <paramref name="percent"/> (clamped 0..100).</summary>
    public static string RingDash(double percent, double radius)
    {
        var circumference = 2 * Math.PI * radius;
        var arc = circumference * Clamp(percent, 0, 100) / 100;
        return arc.ToString("0.##", Invariant) + " " + circumference.ToString("0.##", Invariant);
    }

    private static double? ParseNumber(string s) =>
        double.TryParse(s, NumberStyles.Float, Invariant, out var d) && double.IsFinite(d) ? d : null;

    private static string? FirstLetter(string word)
    {
        var e = StringInfo.GetTextElementEnumerator(word);
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            if (char.IsLetterOrDigit(element, 0))
                return element;
        }

        return null;
    }
}
