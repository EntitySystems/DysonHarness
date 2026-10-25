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
