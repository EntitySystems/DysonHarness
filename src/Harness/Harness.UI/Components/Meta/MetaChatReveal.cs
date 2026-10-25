namespace Harness.UI.Components.Meta;

/// <summary>
/// Reading-speed reveal for a Meta Agent message that arrived while the page was open.
/// History (first build, reload, session switch) is not live.
/// </summary>
public static class MetaChatReveal
{
    /// <summary>
    /// Milliseconds between grapheme clusters.
    /// 238 wpm (average adult silent reading) × 1.2 ≈ 286 wpm ≈ 27.5 chars/s
    /// (286 × 5.7 / 60 ≈ 27.2; 1000/27.5 ≈ 36). Keep in sync with dysonMetaReveal.staggerMs.
    /// </summary>
    public const int CharStaggerMs = 36;

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
