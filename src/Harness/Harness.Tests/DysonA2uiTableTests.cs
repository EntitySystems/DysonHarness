using System.Globalization;
using System.Text.Json.Nodes;
using DysonHarness;
using Xunit;

namespace Harness.Tests;

public sealed class DysonA2uiTableTests
{
    private static DysonA2uiCell Cell(string text) => DysonA2uiTable.Display(JsonValue.Create(text), null);

    private static DysonA2uiCell Num(double n) => DysonA2uiTable.Display(JsonValue.Create(n), null);

    private static IReadOnlyList<DysonA2uiCell> Row(params string[] cells) => cells.Select(Cell).ToArray();

    [Fact]
    public void Sort_NumbersCompareNumericallyNotAsText()
    {
        var order = DysonA2uiTable.Sort([Num(10), Num(9), Num(100)], descending: false);
        Assert.Equal(new[] { 1, 0, 2 }, order);
        Assert.Equal(new[] { 2, 0, 1 }, DysonA2uiTable.Sort([Num(10), Num(9), Num(100)], descending: true));
    }

    [Fact]
    public void Sort_NumericStringsSortAsNumbers()
    {
        Assert.Equal(new[] { 1, 0 }, DysonA2uiTable.Sort([Cell("10"), Cell("9")], descending: false));
    }

    [Fact]
    public void Sort_MixedColumnFallsBackToIgnoreCaseText()
    {
        var order = DysonA2uiTable.Sort([Cell("banana"), Cell("Apple"), Num(5)], descending: false);
        Assert.Equal(new[] { 2, 1, 0 }, order); // "5" < "Apple" < "banana"
    }

    [Fact]
    public void Sort_IsStableAndEmptyCellsStayLast()
    {
        var column = new[] { Cell("b"), Cell(""), Cell("a"), Cell("B"), Cell("") };
        Assert.Equal(new[] { 2, 0, 3, 1, 4 }, DysonA2uiTable.Sort(column, descending: false));
        Assert.Equal(new[] { 0, 3, 2, 1, 4 }, DysonA2uiTable.Sort(column, descending: true));
    }

    [Fact]
    public void Display_FormatsNumbersInvariantAndTolerantOfOddNodes()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("$1,234.50", DysonA2uiTable.Display(JsonValue.Create(1234.5), "currency").Text);
            Assert.Equal("1,234.5", DysonA2uiTable.Display(JsonValue.Create(1234.5), null).Text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Equal("Yes", DysonA2uiTable.Display(JsonValue.Create(true), null).Text);
        Assert.Equal("", DysonA2uiTable.Display(null, null).Text);
        Assert.Equal("", DysonA2uiTable.Display(new JsonObject(), null).Text);
        Assert.Equal("2026-01-05", DysonA2uiTable.Display(JsonValue.Create("2026-01-05T10:00:00Z"), "date").Text);
    }

    [Fact]
    public void Filter_IsOrdinalIgnoreCaseContainsAcrossAllCells()
    {
        var rows = new[] { Row("Alpha", "x"), Row("beta", "ALPHABET"), Row("gamma", "y") };
        Assert.Equal(new[] { 0, 1 }, DysonA2uiTable.Filter(rows, "alpha"));
        Assert.Equal(new[] { 0, 1, 2 }, DysonA2uiTable.Filter(rows, "  "));
        Assert.Empty(DysonA2uiTable.Filter(rows, "zzz"));
    }

    [Fact]
    public void Query_FiltersSortsAndPages()
    {
        IReadOnlyList<IReadOnlyList<DysonA2uiCell>> rows = [Row("c", "3"), Row("a", "1"), Row("b", "2"), Row("d", "4"), Row("e", "5")];
        var first = DysonA2uiTable.Query(rows, null, sortColumn: 1, descending: true, page: 0, pageSize: 2);
        Assert.Equal(new[] { 4, 3 }, first.Rows);
        Assert.Equal(5, first.Matches);
        Assert.Equal(3, first.PageCount);

        var last = DysonA2uiTable.Query(rows, null, 1, true, page: 2, pageSize: 2);
        Assert.Equal(new[] { 1 }, last.Rows);

        var filtered = DysonA2uiTable.Query(rows, "d", 0, false, 0, 2);
        Assert.Equal(new[] { 3 }, filtered.Rows);
        Assert.Equal(1, filtered.PageCount);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(99, 2)]
    public void Query_ClampsPageIntoRange(int requested, int expected)
    {
        IReadOnlyList<IReadOnlyList<DysonA2uiCell>> rows = [Row("a"), Row("b"), Row("c"), Row("d"), Row("e")];
        var page = DysonA2uiTable.Query(rows, null, -1, false, requested, 2);
        Assert.Equal(expected, page.Page);
        Assert.Equal(3, page.PageCount);
    }

    [Fact]
    public void Query_EmptyAndUnpagedNeverThrow()
    {
        var empty = DysonA2uiTable.Query([], "x", 3, false, 7, 10);
        Assert.Empty(empty.Rows);
        Assert.Equal(0, empty.Page);
        Assert.Equal(1, empty.PageCount);

        IReadOnlyList<IReadOnlyList<DysonA2uiCell>> rows = [Row("a"), Row("b")];
        var all = DysonA2uiTable.Query(rows, null, 9, false, 0, 0); // sort column past the row width, no paging
        Assert.Equal(new[] { 0, 1 }, all.Rows);
    }

    [Theory]
    [InlineData("Ada", "A")]
    [InlineData("ada lovelace", "AL")]
    [InlineData("  Grace   Brewster Murray Hopper ", "GB")]
    [InlineData("张伟", "张")]
    [InlineData("李 小龍", "李小")]
    [InlineData("Émile Zola", "ÉZ")]
    [InlineData("", "?")]
    [InlineData("   ", "?")]
    [InlineData("--", "?")]
    public void Initials_FirstLettersOfFirstTwoWords(string name, string expected)
    {
        Assert.Equal(expected, DysonA2uiTable.Initials(name));
    }

    [Fact]
    public void Initials_NullIsPlaceholder() => Assert.Equal("?", DysonA2uiTable.Initials(null));

    [Fact]
    public void ColorIndex_IsStableCaseInsensitiveAndInRange()
    {
        Assert.Equal(DysonA2uiTable.ColorIndex("Ada Lovelace"), DysonA2uiTable.ColorIndex("  ada lovelace "));
        foreach (var name in new[] { "", "a", "Bob", "张伟", "Grace Hopper", "x y z" })
            Assert.InRange(DysonA2uiTable.ColorIndex(name), 0, 5);
        Assert.InRange(DysonA2uiTable.ColorIndex(null), 0, 5);
    }

    [Theory]
    [InlineData(0, "Empty,Empty,Empty,Empty,Empty")]
    [InlineData(3.5, "Full,Full,Full,Half,Empty")]
    [InlineData(3.2, "Full,Full,Full,Empty,Empty")]
    [InlineData(99, "Full,Full,Full,Full,Full")]
    [InlineData(-4, "Empty,Empty,Empty,Empty,Empty")]
    [InlineData(double.NaN, "Empty,Empty,Empty,Empty,Empty")]
    public void Stars_RoundToHalvesAndClamp(double value, string expected)
    {
        Assert.Equal(expected, string.Join(",", DysonA2uiTable.Stars(value)));
    }

    [Fact]
    public void RingDash_IsInvariantAndClamped()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("0 100.53", DysonA2uiTable.RingDash(-10, 16));
            Assert.Equal("100.53 100.53", DysonA2uiTable.RingDash(400, 16));
            Assert.Equal("50.27 100.53", DysonA2uiTable.RingDash(50, 16));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
