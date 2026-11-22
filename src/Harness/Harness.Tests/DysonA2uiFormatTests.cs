using System.Globalization;

using DysonHarness;

namespace Harness.Tests;

/// <summary>Invariant-culture display formatting for charts and tables.</summary>
public class DysonA2uiFormatTests
{
    [Theory]
    [InlineData(1234.5, "number", "1,234.5")]
    [InlineData(1234.5, null, "1,234.5")]
    [InlineData(1234.5, "currency", "$1,234.50")]
    [InlineData(-5, "currency", "-$5.00")]
    [InlineData(12.5, "percent", "12.5%")]
    [InlineData(1234, "compact", "1.2k")]
    [InlineData(3400000, "compact", "3.4M")]
    [InlineData(2500000000, "compact", "2.5B")]
    [InlineData(999, "compact", "999")]
    [InlineData(-1500, "compact", "-1.5k")]
    public void Number_formats_with_invariant_culture(double value, string? format, string expected) =>
        Assert.Equal(expected, DysonA2uiFormat.Number(value, format));

    [Fact]
    public void Number_is_culture_independent()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("$1,234.50", DysonA2uiFormat.Number(1234.5, "currency"));
            Assert.Equal("1.2k", DysonA2uiFormat.Number(1234, "compact"));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Unit_suffix_and_non_finite_values()
    {
        Assert.Equal("5 ms", DysonA2uiFormat.Number(5, "number", "ms"));
        Assert.Equal("", DysonA2uiFormat.Number(double.NaN, "number"));
        Assert.Equal("", DysonA2uiFormat.Number(double.PositiveInfinity, "currency"));
    }

    [Fact]
    public void Date_iso_round_trips_and_unparsable_is_kept()
    {
        Assert.Equal("2026-11-22", DysonA2uiFormat.Date("2026-11-22"));
        Assert.Equal("2026-11-22", DysonA2uiFormat.Date("2026-11-22T10:30:00Z"));
        Assert.Equal("not a date", DysonA2uiFormat.Date("not a date"));
        Assert.Equal("", DysonA2uiFormat.Date(null));
    }
}
