namespace Harness.UI.Components.Meta;

/// <summary>
/// Reading-speed reveal for a Meta Agent message that arrived while the page was open.
/// History (first build, reload, session switch) is not live.
/// </summary>
public static class MetaChatReveal
{
    /// <summary>
    /// Milliseconds between words.
    /// 238 wpm (average adult silent reading) × 1.2 ≈ 286 wpm ≈ 4.76 words/s ≈ 210 ms/word.
    /// About 27 characters/second at ~5.7 characters per word including the trailing space
    /// (286 × 5.7 / 60 ≈ 27). Keep in sync with dysonMetaReveal.staggerMs.
    /// </summary>
    public const int WordStaggerMs = 210;

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
