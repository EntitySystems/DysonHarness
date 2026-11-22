using System.Globalization;

namespace DysonHarness;

/// <summary>
/// Display formatting for charts and tables: number, currency (en-US "$1,234.50"), percent, compact
/// (1.2k, 3.4M, 5.6B) and date (yyyy-MM-dd). Invariant culture only; never throws.
/// </summary>
public static class DysonA2uiFormat
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Unknown formats and null format fall back to <c>number</c>. Non-finite values give "".</summary>
    public static string Number(double value, string? format, string? unit = null)
    {
        if (!double.IsFinite(value))
            return "";
        var text = format switch
        {
            "currency" => Currency(value),
            "percent" => Round(value) + "%",
            "compact" => Compact(value),
            _ => Round(value, grouping: true),
        };
        return string.IsNullOrEmpty(unit) ? text : format is "currency" or "percent" ? text : text + " " + unit;
    }

    public static string Currency(double value)
    {
        var abs = Math.Abs(value).ToString("N2", Invariant);
        return (value < 0 ? "-$" : "$") + abs;
    }

    /// <summary>1234 → 1.2k, 3400000 → 3.4M. Below 1000 the number is kept as is (up to 2 decimals).</summary>
    public static string Compact(double value)
    {
        var abs = Math.Abs(value);
        var sign = value < 0 ? "-" : "";
        (double Divisor, string Suffix)[] steps = [(1e12, "T"), (1e9, "B"), (1e6, "M"), (1e3, "k")];
        foreach (var (divisor, suffix) in steps)
        {
            if (abs >= divisor)
                return sign + (abs / divisor).ToString("0.#", Invariant) + suffix;
        }

        return Round(value);
    }

    /// <summary>ISO 8601 text (date or date-time) → <c>yyyy-MM-dd</c>; anything unparsable is returned as given.</summary>
    public static string Date(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
            return "";
        return DateTimeOffset.TryParse(iso, Invariant, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToString("yyyy-MM-dd", Invariant)
            : iso;
    }

    private static string Round(double value, bool grouping = false) =>
        value.ToString(grouping ? "#,##0.##" : "0.##", Invariant);
}
