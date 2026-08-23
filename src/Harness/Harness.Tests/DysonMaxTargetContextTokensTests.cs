using DysonHarness;

namespace Harness.Tests;

public class DysonMaxTargetContextTokensTests
{
    [Theory]
    [InlineData(0L, false, "0")]
    [InlineData(0L, true, "Off")]
    [InlineData(-1L, false, "0")]
    [InlineData(-1L, true, "Off")]
    [InlineData(999L, false, "999")]
    [InlineData(1_000L, false, "1K")]
    [InlineData(12_400L, false, "12.4K")]
    [InlineData(25_000L, false, "25K")]
    [InlineData(100_000L, false, "100K")]
    [InlineData(1_000_000L, false, "1M")]
    [InlineData(1_400_000L, false, "1.4M")]
    [InlineData(9_855_700L, false, "9.9M")]
    [InlineData(3_000_000_000L, false, "3B")]
    public void FormatCompact_FormatsTokenCounts(long tokens, bool zeroAsOff, string expected)
    {
        Assert.Equal(expected, DysonMaxTargetContextTokens.FormatCompact(tokens, zeroAsOff));
    }

    [Fact]
    public void FormatCompact_IntOverload_DelegatesToLongOverload()
    {
        Assert.Equal("12.4K", DysonMaxTargetContextTokens.FormatCompact(12_400));
    }
}
