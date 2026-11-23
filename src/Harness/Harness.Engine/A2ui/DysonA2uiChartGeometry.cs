using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using static DysonHarness.DysonA2uiChartKind;

namespace DysonHarness;

public enum DysonA2uiChartKind { Bar, Line, Area, Pie, Donut, Sparkline, Gauge }

/// <summary>Resolved chart settings. <see cref="ShowLegend"/> null = automatic (more than one series, or pie).</summary>
public sealed record DysonA2uiChartOptions(
    DysonA2uiChartKind Kind,
    bool Stacked = false,
    bool Horizontal = false,
    bool ShowDots = false,
    int Height = DysonA2uiChartGeometry.DefaultHeight,
    string? Format = null,
    string? Unit = null,
    bool? ShowLegend = null);

public sealed record DysonA2uiChartAxis(double Min, double Max, double Step, IReadOnlyList<double> Ticks);

public sealed record DysonA2uiChartLine(string X1, string Y1, string X2, string Y2);

/// <summary><see cref="Anchor"/> is one of start, middle, end (a fixed class in the UI).</summary>
public sealed record DysonA2uiChartText(string Text, string X, string Y, string Anchor);

/// <summary><see cref="Series"/> is the palette index 0..7. <see cref="FillD"/> is set for area charts and sparklines.</summary>
public sealed record DysonA2uiMapPoint(string X, string Y);

/// <summary>
/// Map without tiles: markers (lat, lng) fitted equirectangular into a 240x140 box. <see cref="Points"/> lines up
/// with the input; <see cref="RoutePoints"/> is the polyline in input order. Numbers are invariant text.
/// </summary>
public sealed record DysonA2uiMapPlot(IReadOnlyList<DysonA2uiMapPoint> Points, string RoutePoints)
{
    public const int Width = 240;
    public const int Height = 140;
    private const double Pad = 14;

    public static DysonA2uiMapPlot Empty { get; } = new([], "");

    public string ViewBox => $"0 0 {Width} {Height}";

    public static DysonA2uiMapPlot Build(IReadOnlyList<(double Lat, double Lng)> markers)
    {
        if (markers.Count == 0)
            return Empty;
        double minLat = markers.Min(m => m.Lat), maxLat = markers.Max(m => m.Lat);
        double minLng = markers.Min(m => m.Lng), maxLng = markers.Max(m => m.Lng);
        // Degrees of longitude shrink with latitude; keep the aspect so the plot is not stretched.
        var scaleX = Math.Cos((minLat + maxLat) / 2 * Math.PI / 180);
        double spanX = Math.Max((maxLng - minLng) * scaleX, 1e-9), spanY = Math.Max(maxLat - minLat, 1e-9);
        var scale = Math.Min((Width - 2 * Pad) / spanX, (Height - 2 * Pad) / spanY);
        double offX = (Width - spanX * scale) / 2, offY = (Height - spanY * scale) / 2;
        var points = new List<DysonA2uiMapPoint>(markers.Count);
        foreach (var (lat, lng) in markers)
        {
            var x = maxLng == minLng ? Width / 2.0 : offX + (lng - minLng) * scaleX * scale;
            var y = maxLat == minLat ? Height / 2.0 : offY + (maxLat - lat) * scale;
            points.Add(new DysonA2uiMapPoint(DysonA2uiChartGeometry.F(x), DysonA2uiChartGeometry.F(y)));
        }

        return new DysonA2uiMapPlot(points, string.Join(' ', points.Select(p => p.X + "," + p.Y)));
    }
}

public sealed record DysonA2uiChartPath(int Series, string D, string? FillD);

public sealed record DysonA2uiChartDot(int Series, string X, string Y, string Title);

public sealed record DysonA2uiChartRect(int Series, string X, string Y, string Width, string Height, string Title);

/// <summary><see cref="Tone"/> is 0..2: pie slices past the 8-colour palette repeat it with a lighter tone.</summary>
public sealed record DysonA2uiChartArc(int Series, int Tone, string D, double Degrees, string Title);

public sealed record DysonA2uiChartLegendItem(int Series, int Tone, string Label);

/// <summary>
/// Pure chart geometry: resolved spec to viewBox, nice 5-tick axes, series paths, bar rects, pie/donut arcs,
/// gauge ring, legend and per-point titles. Every coordinate is a number formatted here with the invariant
/// culture, so the UI only emits SVG elements and never an agent string in an attribute. Never throws.
/// </summary>
public sealed record DysonA2uiChartGeometry
{
    public const int PaletteSize = 8;
    public const int DefaultHeight = 240;
    public const int MinHeight = 120;
    public const int MaxHeight = 480;
    public const string NoData = "No data";

    private const int Width = 480;
    private const int SparkWidth = 120;
    private const int SparkHeight = 24;
    private const int PieSize = 200;
    private const int MaxLabelChars = 14;

    public DysonA2uiChartKind Kind { get; init; }
    public string ViewBox { get; init; } = $"0 0 {Width} {DefaultHeight}";
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public bool IsEmpty { get; init; }
    public int Height { get; init; } = DefaultHeight;

    /// <summary>Gauge caption (the <c>label</c> prop).</summary>
    public string? Caption { get; init; }

    /// <summary>"No data" when <see cref="IsEmpty"/>, else null.</summary>
    public string? Message { get; init; }

    public bool Truncated { get; init; }
    public string? TruncationNote { get; init; }

    /// <summary>NaN, Infinity, non-numbers and (pie) non-positive values that were not plotted.</summary>
    public int Dropped { get; init; }

    public bool ShowDots { get; init; }
    public bool ShowLegend { get; init; }
    public IReadOnlyList<DysonA2uiChartLine> Grid { get; init; } = [];
    public IReadOnlyList<DysonA2uiChartText> Labels { get; init; } = [];
    public IReadOnlyList<DysonA2uiChartPath> Paths { get; init; } = [];
    public IReadOnlyList<DysonA2uiChartDot> Dots { get; init; } = [];
    public IReadOnlyList<DysonA2uiChartRect> Rects { get; init; } = [];
    public IReadOnlyList<DysonA2uiChartArc> Arcs { get; init; } = [];
    public IReadOnlyList<DysonA2uiChartLegendItem> Legend { get; init; } = [];

    /// <summary>Data table for screen readers: header cells, then one row per category (or slice).</summary>
    public IReadOnlyList<string> TableHeader { get; init; } = [];

    public IReadOnlyList<IReadOnlyList<string>> TableRows { get; init; } = [];

    /// <summary>Gauge only: the track and value half-ring path data (ValueD is "" at the minimum) and the centre text.</summary>
    public string? TrackD { get; init; }

    public string? ValueD { get; init; }
    public string? CenterText { get; init; }

    public static int ClampHeight(int height) => Math.Clamp(height, MinHeight, MaxHeight);

    /// <summary>Chart kind for a component name (and <c>chartType</c> for <c>Chart</c>); null for non-chart names.</summary>
    public static DysonA2uiChartKind? KindOf(string? component, string? chartType) => component switch
    {
        "Chart" => chartType switch { "line" => Line, "area" => Area, "pie" => Pie, "donut" => Donut, _ => Bar },
        "BarChart" => Bar,
        "LineChart" => Line,
        "AreaChart" => Area,
        "PieChart" => Pie,
        "Sparkline" => Sparkline,
        "Gauge" => Gauge,
        _ => null,
    };

    /// <summary>
    /// Component JSON + data model to geometry. <c>data</c> may be bound; it is resolved here, pivoted by
    /// <see cref="DysonA2uiChart.Normalize"/> and capped there. Unknown names give an empty geometry.
    /// </summary>
    public static DysonA2uiChartGeometry FromComponent(JsonObject c, JsonNode model, string scope)
    {
        var name = DysonA2uiValidator.AsString(c["component"]);
        var kind = KindOf(name, DysonA2uiBinding.ResolveString(c["chartType"], model, scope));
        if (kind is null)
            return Empty(Bar, DefaultHeight, null, false, 0);

        var format = Blank(DysonA2uiBinding.ResolveString(c["format"], model, scope));
        var unit = Blank(DysonA2uiBinding.ResolveString(c["unit"], model, scope));
        var title = Blank(DysonA2uiBinding.ResolveString(c["title"], model, scope));
        var subtitle = Blank(DysonA2uiBinding.ResolveString(c["subtitle"], model, scope));
        if (kind == Gauge)
        {
            var gauge = BuildGauge(
                DysonA2uiBinding.ResolveNumber(c["value"], model, scope) ?? double.NaN,
                DysonA2uiBinding.ResolveNumber(c["min"], model, scope) ?? 0,
                DysonA2uiBinding.ResolveNumber(c["max"], model, scope) ?? 100,
                format,
                unit,
                Blank(DysonA2uiBinding.ResolveString(c["label"], model, scope)));
            return gauge with { Title = title, Subtitle = subtitle };
        }

        var pie = kind is Pie or Donut;
        var xKey = Blank(DysonA2uiBinding.ResolveString(c["xKey"], model, scope));
        var series = ParseSeries(DysonA2uiBinding.ResolveValue(c["series"], model, scope));
        var raw = DysonA2uiBinding.ResolveValue(c["data"], model, scope);
        if (pie)
            raw = PieRows(raw, xKey, series?[0].Key);
        if (kind == Pie && DysonA2uiBinding.ResolveNumber(c["innerRadius"], model, scope) is > 0)
            kind = Donut;
        var options = new DysonA2uiChartOptions(
            kind.Value,
            DysonA2uiBinding.ResolveBool(c["stacked"], model, scope),
            DysonA2uiBinding.ResolveString(c["layout"], model, scope) == "horizontal",
            DysonA2uiBinding.ResolveBool(c["showDots"], model, scope),
            DysonA2uiBinding.ResolveNumber(c["height"], model, scope) is { } h ? (int)Math.Round(h) : DefaultHeight,
            format,
            unit,
            c.ContainsKey("showLegend") ? DysonA2uiBinding.ResolveBool(c["showLegend"], model, scope) : (bool?)null);
        var data = DysonA2uiChart.Normalize(raw, xKey, series, pie);
        return Build(data, options) with { Title = title, Subtitle = subtitle };
    }

    public static DysonA2uiChartGeometry Build(DysonA2uiChartData data, DysonA2uiChartOptions options)
    {
        var height = ClampHeight(options.Height);
        if (data.IsEmpty || options.Kind == Gauge)
            return Empty(options.Kind, height, data, options.Kind == Sparkline, data.Dropped);
        return options.Kind is Pie or Donut ? PieChart(data, options) : Cartesian(data, options, height);
    }

    /// <summary>Nice-number axis (Heckbert): about 5 ticks, ends rounded outwards. Non-finite input gives 0..1.</summary>
    public static DysonA2uiChartAxis NiceTicks(double min, double max, int maxTicks = 5)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max))
            (min, max) = (0, 1);
        if (max < min)
            (min, max) = (max, min);
        if (max == min)
        {
            if (min == 0)
                max = 1;
            else
            {
                var pad = Math.Abs(min) * 0.5;
                (min, max) = (min - pad, max + pad);
            }
        }

        var range = NiceNum(max - min, false);
        var step = NiceNum(range / Math.Max(1, maxTicks - 1), true);
        var lo = Math.Floor(min / step) * step;
        var hi = Math.Ceiling(max / step) * step;
        var count = Math.Clamp((int)Math.Round((hi - lo) / step), 1, 50);
        var ticks = new List<double>(count + 1);
        for (var i = 0; i <= count; i++)
            ticks.Add(Math.Round(lo + i * step, 10));
        return new DysonA2uiChartAxis(ticks[0], ticks[^1], step, ticks);
    }

    private static double NiceNum(double x, bool round)
    {
        var exp = Math.Floor(Math.Log10(x));
        var f = x / Math.Pow(10, exp);
        var nf = round
            ? f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10
            : f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
        return nf * Math.Pow(10, exp);
    }

    /// <summary>Invariant coordinate text: up to 2 decimals, never "-0", never NaN.</summary>
    internal static string F(double v)
    {
        if (!double.IsFinite(v))
            return "0";
        var r = Math.Round(v, 2);
        return r == 0 ? "0" : r.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string Fmt(double v, DysonA2uiChartOptions o) => DysonA2uiFormat.Number(v, o.Format, o.Unit);

    private static string Short(string s)
    {
        if (s.Length <= MaxLabelChars)
            return s;
        var cut = MaxLabelChars - 1;
        if (char.IsHighSurrogate(s[cut - 1]))
            cut--;
        return s[..cut] + "…";
    }

    private static string TickLabel(double v, string? format) => format switch
    {
        "currency" => (v < 0 ? "-$" : "$") + DysonA2uiFormat.Compact(Math.Abs(v)),
        "percent" => DysonA2uiFormat.Number(v, "percent"),
        _ => DysonA2uiFormat.Compact(v),
    };

    private static IReadOnlyList<(string Key, string Label)>? ParseSeries(JsonNode? node)
    {
        if (node is not JsonArray array)
            return null;
        var list = new List<(string, string)>();
        foreach (var item in array)
        {
            if (DysonA2uiValidator.AsString(item) is { } key)
                list.Add((key, key));
            else if (item is JsonObject o && DysonA2uiValidator.AsString(o["key"]) is { } k)
                list.Add((k, DysonA2uiValidator.AsString(o["label"]) ?? k));
        }

        return list.Count == 0 ? null : list;
    }

    /// <summary>
    /// Standard-shape pie data (<c>xKey</c> + a numeric field) becomes <c>{label, value}</c> rows. Rows that
    /// already have <c>value</c> pass through unchanged.
    /// </summary>
    private static JsonNode? PieRows(JsonNode? data, string? xKey, string? seriesKey)
    {
        if (data is not JsonArray rows || rows.OfType<JsonObject>().FirstOrDefault() is not { } first || first.ContainsKey("value"))
            return data;
        var labelKey = xKey ?? first.FirstOrDefault(p => DysonA2uiValidator.AsString(p.Value) is not null).Key ?? "label";
        var valueKey = seriesKey
            ?? first.FirstOrDefault(p => p.Key != labelKey && p.Value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number).Key;
        if (valueKey is null)
            return data;
        var result = new JsonArray();
        foreach (var row in rows.OfType<JsonObject>())
            result.Add(new JsonObject { ["label"] = row[labelKey]?.DeepClone(), ["value"] = row[valueKey]?.DeepClone() });
        return result;
    }

    private static DysonA2uiChartGeometry Empty(DysonA2uiChartKind kind, int height, DysonA2uiChartData? data, bool spark, int dropped) => new()
    {
        Kind = kind,
        Height = height,
        ViewBox = spark ? $"0 0 {SparkWidth} {SparkHeight}" : $"0 0 {Width} {height}",
        IsEmpty = true,
        Message = NoData,
        Truncated = data?.Truncated ?? false,
        Dropped = dropped,
    };

    private static string? Note(DysonA2uiChartData data) =>
        data.Truncated ? $"showing first {data.Categories.Count.ToString(CultureInfo.InvariantCulture)} points" : null;

    private static DysonA2uiChartGeometry Cartesian(DysonA2uiChartData data, DysonA2uiChartOptions o, int height)
    {
        var kind = o.Kind;
        var spark = kind == Sparkline;
        var series = spark ? data.Series.Take(1).ToList() : data.Series.ToList();
        var cats = data.Categories;
        var n = cats.Count;
        var count = series.Count;
        var horizontal = kind == Bar && o.Horizontal;
        var stacked = o.Stacked && count > 1 && kind is Bar or Area;
        var filled = kind is Area || spark;

        // lower/upper edge per series and category; null = no value (a gap).
        var lower = new double?[count, n];
        var upper = new double?[count, n];
        for (var i = 0; i < n; i++)
        {
            double pos = 0, neg = 0;
            for (var s = 0; s < count; s++)
            {
                var v = series[s].Values[i];
                if (stacked)
                {
                    var x = v ?? 0;
                    if (x >= 0)
                        (lower[s, i], upper[s, i], pos) = (pos, pos + x, pos + x);
                    else
                        (lower[s, i], upper[s, i], neg) = (neg, neg + x, neg + x);
                }
                else if (v is { } plain)
                {
                    (lower[s, i], upper[s, i]) = (0, plain);
                }
            }
        }

        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var v in upper)
        {
            if (v is { } t)
                (lo, hi) = (Math.Min(lo, t), Math.Max(hi, t));
        }

        if (stacked)
        {
            foreach (var v in lower)
            {
                if (v is { } t)
                    (lo, hi) = (Math.Min(lo, t), Math.Max(hi, t));
            }
        }

        if (lo > hi)
            return Empty(kind, height, data, spark, data.Dropped);
        if (kind != Line && !spark)
            (lo, hi) = (Math.Min(lo, 0), Math.Max(hi, 0));
        var axis = spark
            ? new DysonA2uiChartAxis(lo, hi == lo ? lo + 1 : hi, 0, [])
            : NiceTicks(lo, hi);
        var span = axis.Max - axis.Min;

        double w = spark ? SparkWidth : Width, h = spark ? SparkHeight : height;
        double left = spark ? 2 : horizontal ? 84 : 44, right = spark ? 2 : 12, top = spark ? 2 : 12, bottom = spark ? 2 : horizontal ? 20 : 28;
        double pw = w - left - right, ph = h - top - bottom;
        double ValueY(double v) => top + ph - (v - axis.Min) / span * ph;
        double ValueX(double v) => left + (v - axis.Min) / span * pw;
        double CatX(int i) => left + (i + 0.5) * pw / n;
        double CatY(int i) => top + (i + 0.5) * ph / n;

        var grid = new List<DysonA2uiChartLine>();
        var labels = new List<DysonA2uiChartText>();
        if (!spark)
        {
            foreach (var t in axis.Ticks)
            {
                var text = TickLabel(t, o.Format);
                if (horizontal)
                {
                    var x = F(ValueX(t));
                    grid.Add(new(x, F(top), x, F(top + ph)));
                    labels.Add(new(text, x, F(h - 6), "middle"));
                }
                else
                {
                    var y = F(ValueY(t));
                    grid.Add(new(F(left), y, F(left + pw), y));
                    labels.Add(new(text, F(left - 6), F(ValueY(t) + 4), "end"));
                }
            }

            var room = horizontal ? ph / 16 : pw / 56;
            var every = Math.Max(1, (int)Math.Ceiling(n / Math.Max(1, Math.Floor(room))));
            for (var i = 0; i < n; i += every)
            {
                labels.Add(horizontal
                    ? new DysonA2uiChartText(Short(cats[i]), F(left - 6), F(CatY(i) + 4), "end")
                    : new DysonA2uiChartText(Short(cats[i]), F(CatX(i)), F(h - 8), "middle"));
            }
        }

        string PointTitle(int s, int i, double v) =>
            count > 1 ? $"{cats[i]}: {series[s].Label} {Fmt(v, o)}" : $"{cats[i]}: {Fmt(v, o)}";

        var paths = new List<DysonA2uiChartPath>();
        var dots = new List<DysonA2uiChartDot>();
        var rects = new List<DysonA2uiChartRect>();
        if (kind == Bar)
        {
            var band = (horizontal ? ph : pw) / n;
            var group = band * 0.8;
            var barW = stacked ? group : group / count;
            for (var s = 0; s < count; s++)
            {
                for (var i = 0; i < n; i++)
                {
                    if (upper[s, i] is not { } b || lower[s, i] is not { } a || a == b)
                        continue;
                    var along = i * band + (band - group) / 2 + (stacked ? 0 : s * barW);
                    var title = PointTitle(s, i, series[s].Values[i] ?? 0);
                    var thick = F(Math.Max(0.5, barW - (stacked ? 0 : 1)));
                    if (horizontal)
                    {
                        double x1 = ValueX(a), x2 = ValueX(b);
                        rects.Add(new(s % PaletteSize, F(Math.Min(x1, x2)), F(top + along), F(Math.Abs(x2 - x1)), thick, title));
                    }
                    else
                    {
                        double y1 = ValueY(a), y2 = ValueY(b);
                        rects.Add(new(s % PaletteSize, F(left + along), F(Math.Min(y1, y2)), thick, F(Math.Abs(y2 - y1)), title));
                    }
                }
            }
        }
        else
        {
            for (var s = 0; s < count; s++)
            {
                var line = new StringBuilder();
                var fill = new StringBuilder();
                var run = new List<int>();

                void Flush()
                {
                    if (run.Count == 0)
                        return;
                    foreach (var i in run)
                        line.Append(i == run[0] ? "M" : " L").Append(F(CatX(i))).Append(' ').Append(F(ValueY(upper[s, i]!.Value)));
                    line.Append(' ');
                    if (filled)
                    {
                        foreach (var i in run)
                            fill.Append(i == run[0] ? "M" : " L").Append(F(CatX(i))).Append(' ').Append(F(ValueY(upper[s, i]!.Value)));
                        for (var k = run.Count - 1; k >= 0; k--)
                        {
                            var i = run[k];
                            var baseY = spark ? top + ph : ValueY(lower[s, i]!.Value);
                            fill.Append(" L").Append(F(CatX(i))).Append(' ').Append(F(baseY));
                        }

                        fill.Append(" Z ");
                    }

                    run.Clear();
                }

                for (var i = 0; i < n; i++)
                {
                    if (upper[s, i] is null)
                    {
                        Flush();
                        continue;
                    }

                    run.Add(i);
                    if (o.ShowDots && !spark && kind == Line)
                        dots.Add(new(s % PaletteSize, F(CatX(i)), F(ValueY(upper[s, i]!.Value)), PointTitle(s, i, series[s].Values[i] ?? 0)));
                }

                Flush();
                if (line.Length > 0)
                    paths.Add(new(s % PaletteSize, line.ToString().TrimEnd(), filled ? fill.ToString().TrimEnd() : null));
            }
        }

        var header = new List<string> { "Category" };
        header.AddRange(series.Select(s => s.Label));
        var rows = new List<IReadOnlyList<string>>(n);
        for (var i = 0; i < n; i++)
        {
            var row = new List<string>(count + 1) { cats[i] };
            row.AddRange(series.Select(s => s.Values[i] is { } v ? Fmt(v, o) : ""));
            rows.Add(row);
        }

        return new DysonA2uiChartGeometry
        {
            Kind = kind,
            Height = spark ? SparkHeight : height,
            ViewBox = $"0 0 {F(w)} {F(h)}",
            Truncated = data.Truncated,
            TruncationNote = Note(data),
            Dropped = data.Dropped,
            ShowDots = o.ShowDots,
            ShowLegend = !spark && (o.ShowLegend ?? count > 1),
            Grid = grid,
            Labels = labels,
            Paths = paths,
            Dots = dots,
            Rects = rects,
            Legend = series.Select((s, i) => new DysonA2uiChartLegendItem(i % PaletteSize, 0, s.Label)).ToList(),
            TableHeader = header,
            TableRows = rows,
        };
    }

    private static DysonA2uiChartGeometry PieChart(DysonA2uiChartData data, DysonA2uiChartOptions o)
    {
        const double cx = PieSize / 2.0, cy = PieSize / 2.0, radius = PieSize / 2.0 - 8, inner = radius * 0.58;
        var values = data.Series[0].Values;
        var slices = new List<(string Label, double Value, int Index)>();
        var dropped = data.Dropped;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is not { } v)
                continue;
            if (v > 0)
                slices.Add((data.Categories[i], v, i));
            else
                dropped++;
        }

        var total = slices.Sum(s => s.Value);
        if (slices.Count == 0 || total <= 0)
            return Empty(o.Kind, DefaultHeight, data, false, dropped) with { Height = ClampHeight(o.Height), ViewBox = $"0 0 {PieSize} {PieSize}" };

        var donut = o.Kind == Donut;
        var arcs = new List<DysonA2uiChartArc>();
        var legend = new List<DysonA2uiChartLegendItem>();
        var rows = new List<IReadOnlyList<string>>();
        var start = 0.0;
        for (var k = 0; k < slices.Count; k++)
        {
            var (label, value, _) = slices[k];
            var degrees = value / total * 360;
            var pct = (value / total * 100).ToString("0.#", CultureInfo.InvariantCulture);
            var text = Fmt(value, o);
            arcs.Add(new(k % PaletteSize, k / PaletteSize, ArcPath(cx, cy, radius, donut ? inner : 0, start, degrees), degrees, $"{label}: {text} ({pct}%)"));
            legend.Add(new(k % PaletteSize, k / PaletteSize, label));
            rows.Add([label, text, pct + "%"]);
            start += degrees;
        }

        return new DysonA2uiChartGeometry
        {
            Kind = o.Kind,
            Height = ClampHeight(o.Height),
            ViewBox = $"0 0 {PieSize} {PieSize}",
            Truncated = data.Truncated,
            TruncationNote = data.Truncated ? $"showing first {slices.Count.ToString(CultureInfo.InvariantCulture)} slices" : null,
            Dropped = dropped,
            ShowLegend = o.ShowLegend ?? true,
            Arcs = arcs,
            Legend = legend,
            TableHeader = ["Slice", "Value", "Share"],
            TableRows = rows,
        };
    }

    /// <summary>Slice path from <paramref name="start"/> degrees clockwise from 12 o'clock. <paramref name="inner"/> 0 is a pie wedge.</summary>
    internal static string ArcPath(double cx, double cy, double radius, double inner, double start, double degrees)
    {
        (double X, double Y) At(double rad, double deg)
        {
            var a = (deg - 90) * Math.PI / 180;
            return (cx + rad * Math.Cos(a), cy + rad * Math.Sin(a));
        }

        string P((double X, double Y) p) => F(p.X) + " " + F(p.Y);
        var r = F(radius);
        if (degrees >= 359.99)
        {
            var full = $"M{P(At(radius, 0))} A{r} {r} 0 1 1 {P(At(radius, 180))} A{r} {r} 0 1 1 {P(At(radius, 0))} Z";
            if (inner <= 0)
                return full;
            var ri = F(inner);
            return full + $" M{P(At(inner, 0))} A{ri} {ri} 0 1 0 {P(At(inner, 180))} A{ri} {ri} 0 1 0 {P(At(inner, 0))} Z";
        }

        var large = degrees > 180 ? 1 : 0;
        var end = start + degrees;
        if (inner <= 0)
            return $"M{F(cx)} {F(cy)} L{P(At(radius, start))} A{r} {r} 0 {large} 1 {P(At(radius, end))} Z";
        var rin = F(inner);
        return $"M{P(At(radius, start))} A{r} {r} 0 {large} 1 {P(At(radius, end))} L{P(At(inner, end))} A{rin} {rin} 0 {large} 0 {P(At(inner, start))} Z";
    }

    /// <summary>Half-ring gauge (value between min and max). Non-finite value gives the "No data" geometry.</summary>
    public static DysonA2uiChartGeometry BuildGauge(double value, double min, double max, string? format, string? unit, string? caption)
    {
        const double cx = 100, cy = 100, radius = 80;
        if (!double.IsFinite(value))
            return Empty(Gauge, 120, null, false, 1) with { ViewBox = "0 0 200 120", Height = 120 };
        if (!double.IsFinite(min))
            min = 0;
        if (!double.IsFinite(max) || max <= min)
            max = min + 1;
        var fraction = Math.Clamp((value - min) / (max - min), 0, 1);
        var o = new DysonA2uiChartOptions(Gauge, Format: format, Unit: unit);
        var theta = fraction * Math.PI;
        var end = (X: cx - radius * Math.Cos(theta), Y: cy - radius * Math.Sin(theta));
        var r = F(radius);
        return new DysonA2uiChartGeometry
        {
            Kind = Gauge,
            Height = 120,
            ViewBox = "0 0 200 120",
            TrackD = $"M{F(cx - radius)} {F(cy)} A{r} {r} 0 0 1 {F(cx + radius)} {F(cy)}",
            ValueD = fraction <= 0 ? "" : $"M{F(cx - radius)} {F(cy)} A{r} {r} 0 0 1 {F(end.X)} {F(end.Y)}",
            CenterText = Fmt(value, o),
            Caption = caption,
            Labels = [new(Fmt(min, o), F(cx - radius), "116", "middle"), new(Fmt(max, o), F(cx + radius), "116", "middle")],
            TableHeader = ["Value", "Min", "Max"],
            TableRows = [[Fmt(value, o), Fmt(min, o), Fmt(max, o)]],
        };
    }
}
