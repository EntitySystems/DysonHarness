using Harness.UI.Components.Meta;

namespace Harness.Tests;

public class MetaChatRevealTests
{
    [Fact]
    public void CharStagger_is_reading_speed_doubled()
    {
        // 238 wpm × 1.2 × 2 ≈ 572 wpm. ~5.7 characters per word including the trailing space
        // → ~54.3 characters/second. 1000/55 ≈ 18.18 ms per grapheme.
        const double wordsPerMinute = 238d * 1.2 * 2d;
        var charactersPerSecond = wordsPerMinute * 5.7d / 60d;
        var staggerMs = (int)Math.Round(1000d / 55d);

        Assert.InRange(charactersPerSecond, 54d, 55d);
        Assert.Equal(18, staggerMs);
        Assert.Equal(MetaChatReveal.CharStaggerMs, staggerMs);
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
