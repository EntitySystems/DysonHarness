namespace Harness.UI.Components.Meta;

/// <summary>
/// Reading-speed reveal for a Meta Agent message that arrived while the page was open.
/// History (first build, reload, session switch) is not live.
/// </summary>
public static class MetaChatReveal
{
    /// <summary>
    /// Tokens per second. The user asked the reveal to feel like an LLM stream at 80 tok/s.
    /// </summary>
    public const int TokensPerSecond = 80;

    /// <summary>Characters counted per token when turning the rate into a per-grapheme stagger.</summary>
    public const int CharsPerToken = 4;

    /// <summary>
    /// Milliseconds between grapheme clusters.
    /// 80 tok/s × 4 chars/token = 320 chars/s → 1000/320 = 3.125 ms per char.
    /// Fractional on purpose: an int stagger would be 3 ms (~333 chars/s) and drift the rate.
    /// Keep in sync with dysonMetaReveal.staggerMs.
    /// </summary>
    public const double CharStaggerMs = 1000d / (TokensPerSecond * CharsPerToken);

    /// <summary>
    /// True when <paramref name="next"/> is the same conversation as <paramref name="previous"/>
    /// (at least one id kept, so a live append or a trim). An empty previous list (first load, history
    /// arriving late, session switch via an empty frame) or a disjoint one (session switch) is a
    /// reload: every id in it is history, not live.
    /// </summary>
    public static bool Continues(IEnumerable<string> previous, IEnumerable<string> next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);

        return new HashSet<string>(next, StringComparer.Ordinal).Overlaps(previous);
    }

    /// <summary>
    /// Ids already on screen are history. Anything new after <paramref name="primed"/> is live.
    /// The first call only fills <paramref name="seen"/> and returns nothing.
    /// </summary>
    public static IReadOnlyList<string> LiveKeys(
        IEnumerable<string> keys,
        HashSet<string> seen,
        bool primed)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(seen);

        if (!primed)
        {
            foreach (var key in keys)
                seen.Add(key);
            return [];
        }

        var live = new List<string>();
        foreach (var key in keys)
        {
            if (seen.Add(key))
                live.Add(key);
        }

        return live;
    }
}
