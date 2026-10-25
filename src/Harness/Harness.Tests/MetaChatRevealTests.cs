using Harness.UI.Components.Meta;

namespace Harness.Tests;

public class MetaChatRevealTests
{
    [Fact]
    public void CharStagger_is_eighty_tokens_per_second()
    {
        // 80 tok/s × 4 chars/token = 320 chars/s → 3.125 ms per grapheme.
        const double charactersPerSecond = MetaChatReveal.TokensPerSecond * (double)MetaChatReveal.CharsPerToken;
        var staggerMs = 1000d / charactersPerSecond;

        Assert.Equal(80, MetaChatReveal.TokensPerSecond);
        Assert.Equal(4, MetaChatReveal.CharsPerToken);
        Assert.Equal(320d, charactersPerSecond);
        Assert.Equal(3.125d, staggerMs);
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
