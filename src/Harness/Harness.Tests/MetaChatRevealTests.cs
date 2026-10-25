using Harness.UI.Components.Meta;

namespace Harness.Tests;

public class MetaChatRevealTests
{
    [Fact]
    public void WordStagger_is_reading_speed_plus_twenty_percent()
    {
        // 238 wpm × 1.2 = 285.6 wpm. 60_000 / 285.6 ≈ 210.08 ms per word.
        const double wordsPerMinute = 238d * 1.2;
        var staggerMs = (int)Math.Round(60_000d / wordsPerMinute);

        Assert.Equal(210, staggerMs);
        Assert.Equal(MetaChatReveal.WordStaggerMs, staggerMs);

        // ~5.7 characters per word including the trailing space → ~27 characters/second.
        var charactersPerSecond = wordsPerMinute * 5.7d / 60d;
        Assert.InRange(charactersPerSecond, 27d, 28d);
    }

    [Fact]
    public void LiveKeys_primes_history_then_only_new_ids()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var first = MetaChatReveal.LiveKeys(["u:1", "a:1"], seen, primed: false);

        Assert.Empty(first);
        Assert.Equal(2, seen.Count);

        var second = MetaChatReveal.LiveKeys(["u:1", "a:1", "a:2"], seen, primed: true);

        Assert.Equal(["a:2"], second);
        Assert.Contains("a:2", seen);
    }

    [Fact]
    public void LiveKeys_does_not_repeat_a_key_already_live()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        MetaChatReveal.LiveKeys(["a:1"], seen, primed: false);

        var arrived = MetaChatReveal.LiveKeys(["a:1", "a:2"], seen, primed: true);
        var again = MetaChatReveal.LiveKeys(["a:1", "a:2"], seen, primed: true);

        Assert.Equal(["a:2"], arrived);
        Assert.Empty(again);
    }
}
