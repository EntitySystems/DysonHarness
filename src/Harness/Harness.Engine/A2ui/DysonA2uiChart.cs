using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>One plotted series; <see cref="Values"/> lines up with <see cref="DysonA2uiChartData.Categories"/> (null = no value).</summary>
public sealed record DysonA2uiChartSeries(string Key, string Label, IReadOnlyList<double?> Values);

/// <summary>Normalised chart input. <see cref="Dropped"/> counts NaN/Infinity/non-numeric values; <see cref="Truncated"/> is set when a cap cut data.</summary>
public sealed record DysonA2uiChartData(
    IReadOnlyList<string> Categories,
    IReadOnlyList<DysonA2uiChartSeries> Series,
    int Dropped,
    bool Truncated)
{
    public bool IsEmpty => Categories.Count == 0 || Series.Count == 0 || Series.All(s => s.Values.All(v => v is null));
}

/// <summary>
/// Pure chart input handling: the playground <c>{x,y,series?}</c> shape and the standard
/// <c>data + xKey + series</c> shape both become one pivoted <see cref="DysonA2uiChartData"/>. Never throws.
/// </summary>
public static class DysonA2uiChart
{
    public const string DefaultSeries = "value";

    /// <summary>
    /// Index of the first element of a literal data array that breaks a cap (points per series, total points), or null.
    /// </summary>
    public static int? CapViolation(JsonArray data)
    {
        var perSeries = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        for (var i = 0; i < data.Count; i++)
        {
            if (data[i] is not JsonObject row)
                continue;
            var points = IsPlayground(row) ? 1 : Math.Max(1, NumericFieldCount(row));
            var key = IsPlayground(row) ? DysonA2uiValidator.AsString(row["series"]) ?? DefaultSeries : "";
            perSeries[key] = perSeries.GetValueOrDefault(key) + 1;
            total += points;
            if (perSeries[key] > DysonA2uiProtocol.MaxChartPointsPerSeries || total > DysonA2uiProtocol.MaxChartPoints)
                return i;
        }

        return null;
    }

    /// <summary>
    /// Pivots <paramref name="data"/>. <paramref name="xKey"/> and <paramref name="series"/> are optional (standard shape);
    /// <paramref name="pie"/> reads <c>{label|name, value}</c> and keeps at most <see cref="DysonA2uiProtocol.MaxPieSlices"/> slices.
    /// </summary>
    public static DysonA2uiChartData Normalize(
        JsonNode? data,
        string? xKey = null,
        IReadOnlyList<(string Key, string Label)>? series = null,
        bool pie = false)
    {
        var rows = data is JsonArray array ? array.OfType<JsonObject>().ToList() : [];
        var truncated = false;
        var dropped = 0;
        if (rows.Count == 0)
            return new DysonA2uiChartData([], [], 0, false);

        var categories = new List<string>();
        var byKey = new Dictionary<string, List<double?>>(StringComparer.Ordinal);
        var order = new List<string>();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var categoryIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        void Put(string category, string seriesKey, double? value)
        {
            if (value is null)
            {
                dropped++;
                return;
            }

            if (!categoryIndex.TryGetValue(category, out var at))
            {
                var limit = pie ? DysonA2uiProtocol.MaxPieSlices : DysonA2uiProtocol.MaxChartPointsPerSeries;
                if (categories.Count >= limit)
                {
                    truncated = true;
                    return;
                }

                at = categories.Count;
                categoryIndex[category] = at;
                categories.Add(category);
                foreach (var list in byKey.Values)
                    list.Add(null);
            }

            if (!byKey.TryGetValue(seriesKey, out var values))
            {
                if (byKey.Count >= DysonA2uiProtocol.MaxChartSeries)
                {
                    truncated = true;
                    return;
                }

                values = new List<double?>(new double?[categories.Count]);
                byKey[seriesKey] = values;
                order.Add(seriesKey);
            }

            values[at] = value;
        }

        if (pie)
        {
            foreach (var row in rows)
            {
                var label = Text(row["label"]) ?? Text(row["name"]) ?? Text(row[xKey ?? "label"]) ?? "";
                Put(label, DefaultSeries, Number(row["value"]));
            }
        }
        else if (series is null && xKey is null && IsPlayground(rows[0]))
        {
            foreach (var row in rows)
                Put(Text(row["x"]) ?? "", Text(row["series"]) ?? DefaultSeries, Number(row["y"]));
        }
        else
        {
            var x = xKey ?? FirstTextKey(rows[0]) ?? "x";
            var keys = series?.Select(s => s.Key).ToList() ?? NumericKeys(rows, x);
            if (series is not null)
            {
                foreach (var (key, label) in series)
                    labels[key] = label;
            }

            foreach (var row in rows)
            {
                var category = Text(row[x]) ?? "";
                foreach (var key in keys)
                {
                    if (row.ContainsKey(key))
                        Put(category, key, Number(row[key]));
                }
            }
        }

        var total = 0;
        var result = new List<DysonA2uiChartSeries>();
        foreach (var key in order)
        {
            var values = byKey[key];
            while (values.Count < categories.Count)
                values.Add(null);
            var count = values.Count(v => v is not null);
            if (total + count > DysonA2uiProtocol.MaxChartPoints)
            {
                truncated = true;
                break;
            }

            total += count;
            result.Add(new DysonA2uiChartSeries(key, labels.GetValueOrDefault(key, key), values));
        }

        return new DysonA2uiChartData(categories, result, dropped, truncated);
    }

    private static bool IsPlayground(JsonObject row) => row.ContainsKey("x") && row.ContainsKey("y");

    private static int NumericFieldCount(JsonObject row) =>
        row.Count(p => p.Value is JsonValue v && v.GetValueKind() == JsonValueKind.Number);

    private static string? FirstTextKey(JsonObject row)
    {
        foreach (var (key, value) in row)
        {
            if (DysonA2uiValidator.AsString(value) is not null)
                return key;
        }

        return null;
    }

    private static List<string> NumericKeys(List<JsonObject> rows, string xKey)
    {
        var keys = new List<string>();
        foreach (var row in rows)
        {
            foreach (var (key, value) in row)
            {
                if (key != xKey && !keys.Contains(key) && value is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
                    keys.Add(key);
            }
        }

        return keys;
    }

    private static string? Text(JsonNode? node) => node switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString(),
        _ => null,
    };

    /// <summary>Finite double from a JSON number or numeric string; null for NaN, Infinity and everything else.</summary>
    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue v)
            return null;
        double d;
        switch (v.GetValueKind())
        {
            case JsonValueKind.Number when v.TryGetValue<double>(out d):
                break;
            case JsonValueKind.String when double.TryParse(v.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out d):
                break;
            default:
                return null;
        }

        return double.IsFinite(d) ? d : null;
    }
}
