using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using DysonHarness;

namespace Harness.Tests;

/// <summary>Chart geometry: pivot, nice ticks, invariant path text, pie arcs, empty and truncated data. Pure, platform neutral.</summary>
public class DysonA2uiChartTests
{
    private static JsonArray Rows(string json) => (JsonArray)JsonNode.Parse(json)!;

    private static DysonA2uiChartOptions Opts(DysonA2uiChartKind kind, bool stacked = false) => new(kind, Stacked: stacked);

    [Fact]
    public void Playground_shape_pivots_to_the_standard_shape()
    {
        var playground = DysonA2uiChart.Normalize(Rows("""
            [{"x":"Jan","y":1,"series":"a"},{"x":"Jan","y":10,"series":"b"},{"x":"Feb","y":2,"series":"a"},{"x":"Feb","y":20,"series":"b"}]
            """));
        var standard = DysonA2uiChart.Normalize(
            Rows("""[{"m":"Jan","a":1,"b":10},{"m":"Feb","a":2,"b":20}]"""),
            "m",
            [("a", "a"), ("b", "b")]);

        Assert.Equal(standard.Categories, playground.Categories);
        Assert.Equal(standard.Series.Select(s => s.Key), playground.Series.Select(s => s.Key));
        for (var i = 0; i < standard.Series.Count; i++)
            Assert.Equal(standard.Series[i].Values, playground.Series[i].Values);
    }

    [Fact]
    public void Nice_ticks_round_outwards_to_about_five_ticks()
    {
        Assert.Equal(new[] { 0d, 20, 40, 60, 80, 100 }, DysonA2uiChartGeometry.NiceTicks(0, 97).Ticks);
        Assert.Equal(new[] { 0d, 2, 4, 6, 8, 10 }, DysonA2uiChartGeometry.NiceTicks(0, 10).Ticks);
        Assert.Equal(new[] { 0d, 0.2, 0.4, 0.6, 0.8, 1 }, DysonA2uiChartGeometry.NiceTicks(0, 0).Ticks);
        var negative = DysonA2uiChartGeometry.NiceTicks(-3, 7);
        Assert.True(negative.Min <= -3 && negative.Max >= 7);
        Assert.Contains(0d, negative.Ticks);
    }

    [Fact]
    public void Paths_use_invariant_decimals_under_de_DE()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var data = DysonA2uiChart.Normalize(Rows("""[{"x":"a","y":1.5},{"x":"b","y":7.25},{"x":"c","y":3.3}]"""));
            foreach (var kind in new[] { DysonA2uiChartKind.Line, DysonA2uiChartKind.Area, DysonA2uiChartKind.Sparkline })
            {
                var geo = DysonA2uiChartGeometry.Build(data, Opts(kind));
                var path = Assert.Single(geo.Paths);
                Assert.Matches(@"^[ML0-9. \-]+$", path.D);
                Assert.Contains('.', path.D);
                Assert.DoesNotContain(',', path.D);
                if (kind != DysonA2uiChartKind.Line)
                    Assert.Matches(@"^[MLZ0-9. \-]+$", path.FillD!);
            }

            var bars = DysonA2uiChartGeometry.Build(data, Opts(DysonA2uiChartKind.Bar));
            Assert.Equal(3, bars.Rects.Count);
            Assert.All(bars.Rects, r =>
            {
                Assert.Matches(@"^[0-9]+(\.[0-9]+)?$", r.X);
                Assert.Matches(@"^[0-9]+(\.[0-9]+)?$", r.Height);
            });
            Assert.Contains("7.25", bars.Rects[1].Title);
            Assert.Matches(@"^0 0 [0-9.]+ [0-9.]+$", bars.ViewBox);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Pie_arcs_sum_to_360_degrees()
    {
        var data = DysonA2uiChart.Normalize(Rows("""[{"label":"a","value":30},{"label":"b","value":50},{"label":"c","value":20}]"""), pie: true);
        foreach (var kind in new[] { DysonA2uiChartKind.Pie, DysonA2uiChartKind.Donut })
        {
            var geo = DysonA2uiChartGeometry.Build(data, Opts(kind));
            Assert.Equal(3, geo.Arcs.Count);
            Assert.Equal(360, geo.Arcs.Sum(a => a.Degrees), 6);
            Assert.All(geo.Arcs, a => Assert.StartsWith("M", a.D));
            Assert.Equal(3, geo.Legend.Count);
        }
    }

    [Fact]
    public void Single_slice_pie_is_a_full_circle()
    {
        var data = DysonA2uiChart.Normalize(Rows("""[{"label":"only","value":5}]"""), pie: true);
        var arc = Assert.Single(DysonA2uiChartGeometry.Build(data, Opts(DysonA2uiChartKind.Pie)).Arcs);
        Assert.Equal(360, arc.Degrees, 6);
        Assert.Equal(2, arc.D.Split('A').Length - 1);
    }

    [Fact]
    public void Non_finite_values_are_dropped_and_counted()
    {
        var data = DysonA2uiChart.Normalize(Rows("""[{"x":"a","y":1},{"x":"b","y":"NaN"},{"x":"c","y":"Infinity"},{"x":"d","y":3}]"""));
        Assert.Equal(2, data.Dropped);
        var geo = DysonA2uiChartGeometry.Build(data, Opts(DysonA2uiChartKind.Line));
        Assert.False(geo.IsEmpty);
        Assert.Equal(2, geo.Dropped);
        Assert.DoesNotContain("NaN", Assert.Single(geo.Paths).D);
    }

    [Fact]
    public void Empty_or_unplottable_data_gives_no_data_geometry()
    {
        foreach (var json in new[] { "[]", """[{"x":"a","y":"NaN"}]""" })
        {
            var geo = DysonA2uiChartGeometry.Build(DysonA2uiChart.Normalize(Rows(json)), Opts(DysonA2uiChartKind.Bar));
            Assert.True(geo.IsEmpty);
            Assert.Equal("No data", geo.Message);
            Assert.Empty(geo.Paths);
            Assert.Empty(geo.Rects);
        }

        var pie = DysonA2uiChartGeometry.Build(DysonA2uiChart.Normalize(Rows("""[{"label":"a","value":0}]"""), pie: true), Opts(DysonA2uiChartKind.Pie));
        Assert.True(pie.IsEmpty);
        Assert.True(DysonA2uiChartGeometry.BuildGauge(double.NaN, 0, 100, null, null, null).IsEmpty);
    }

    [Fact]
    public void Data_over_the_point_cap_sets_the_truncation_flag()
    {
        var rows = new JsonArray();
        for (var i = 0; i < DysonA2uiProtocol.MaxChartPointsPerSeries + 20; i++)
            rows.Add(new JsonObject { ["x"] = "p" + i.ToString(CultureInfo.InvariantCulture), ["y"] = i });
        var geo = DysonA2uiChartGeometry.Build(DysonA2uiChart.Normalize(rows), Opts(DysonA2uiChartKind.Line));
        Assert.True(geo.Truncated);
        Assert.Equal("showing first 500 points", geo.TruncationNote);
        Assert.Equal(DysonA2uiProtocol.MaxChartPointsPerSeries, geo.TableRows.Count);
    }

    [Fact]
    public void Component_with_bound_data_resolves_and_honours_stacked_and_height()
    {
        var component = (JsonObject)JsonNode.Parse("""
            {"id":"c","component":"AreaChart","data":{"@path":"/d"},"stacked":true,"height":9999,"format":"currency","title":"Revenue"}
            """)!;
        var model = JsonNode.Parse("""{"d":[{"x":"a","y":1,"series":"s1"},{"x":"a","y":2,"series":"s2"},{"x":"b","y":3,"series":"s1"},{"x":"b","y":4,"series":"s2"}]}""")!;
        var geo = DysonA2uiChartGeometry.FromComponent(component, model, "");

        Assert.Equal("Revenue", geo.Title);
        Assert.Equal(DysonA2uiChartKind.Area, geo.Kind);
        Assert.Equal(2, geo.Paths.Count);
        Assert.Equal(DysonA2uiChartGeometry.MaxHeight, geo.Height);
        Assert.True(geo.ShowLegend);
        Assert.Equal(new[] { "Category", "s1", "s2" }, geo.TableHeader);
        Assert.Equal("$1.00", geo.TableRows[0][1]);
        Assert.Contains(geo.Labels, l => l.Text.StartsWith('$'));
    }

    [Fact]
    public void Chart_type_maps_to_kind_and_gauge_builds_a_half_ring()
    {
        Assert.Equal(DysonA2uiChartKind.Donut, DysonA2uiChartGeometry.KindOf("Chart", "donut"));
        Assert.Equal(DysonA2uiChartKind.Bar, DysonA2uiChartGeometry.KindOf("Chart", null));
        Assert.Null(DysonA2uiChartGeometry.KindOf("Text", null));

        var gauge = DysonA2uiChartGeometry.BuildGauge(75, 0, 100, "percent", null, "CPU");
        Assert.False(gauge.IsEmpty);
        Assert.Equal("75%", gauge.CenterText);
        Assert.Matches(@"^M[0-9. \-]+A[0-9. \-]+$", gauge.ValueD!);
        Assert.Equal("", DysonA2uiChartGeometry.BuildGauge(0, 0, 100, null, null, null).ValueD);
    }

    [Fact]
    public void Map_plot_fits_markers_into_the_box_with_invariant_numbers()
    {
        var plot = DysonA2uiMapPlot.Build([(52.52, 13.405), (48.137, 11.575), (50.11, 8.68)]);
        Assert.Equal(3, plot.Points.Count);
        Assert.All(plot.Points, p =>
        {
            Assert.Matches(@"^[0-9]+(\.[0-9]+)?$", p.X);
            Assert.Matches(@"^[0-9]+(\.[0-9]+)?$", p.Y);
            Assert.InRange(double.Parse(p.X, CultureInfo.InvariantCulture), 0, DysonA2uiMapPlot.Width);
            Assert.InRange(double.Parse(p.Y, CultureInfo.InvariantCulture), 0, DysonA2uiMapPlot.Height);
        });
        Assert.Equal(2, plot.RoutePoints.Count(ch => ch == ' '));
        Assert.Empty(DysonA2uiMapPlot.Build([]).Points);
    }
}
