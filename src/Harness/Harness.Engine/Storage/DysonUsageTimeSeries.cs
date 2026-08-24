namespace DysonHarness;

/// <summary>
/// UTC-aligned token chart data built from usage rows. The type is pure and has no UI dependency.
/// </summary>
public sealed class DysonUsageTimeSeries
{
    private DysonUsageTimeSeries(
        DateTime fromUtc,
        DateTime toUtc,
        DysonUsageTimeSeriesBucket bucket,
        IReadOnlyList<DysonUsageTimeSeriesPoint> points)
    {
        FromUtc = fromUtc;
        ToUtc = toUtc;
        Bucket = bucket;
        Points = points;
    }

    /// <summary>The inclusive UTC window requested by the caller.</summary>
    public DateTime FromUtc { get; }

    /// <summary>The inclusive UTC window requested by the caller.</summary>
    public DateTime ToUtc { get; }

    public DysonUsageTimeSeriesBucket Bucket { get; }

    /// <summary>
    /// One point for every UTC-aligned bucket intersecting the requested window, including zeroes.
    /// </summary>
    public IReadOnlyList<DysonUsageTimeSeriesPoint> Points { get; }

    /// <summary>
    /// Builds a zero-filled time series from already-selected rows. Rows outside the inclusive
    /// requested window are ignored so callers may pass a wider in-memory collection safely.
    /// </summary>
    public static Result<DysonUsageTimeSeries, string> FromRows(
        IEnumerable<DysonUsageRequestEntity> rows,
        DateTime fromUtc,
        DateTime toUtc,
        DysonUsageTimeSeriesBucket bucket)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (!Enum.IsDefined(bucket))
            return Result<DysonUsageTimeSeries, string>.AsError("Usage time-series bucket must be Hour or Day.");

        var normalizedFrom = NormalizeUtc(fromUtc);
        var normalizedTo = NormalizeUtc(toUtc);
        if (normalizedFrom > normalizedTo)
            return Result<DysonUsageTimeSeries, string>.AsError("Usage time-series fromUtc must be on or before toUtc.");

        var start = Align(normalizedFrom, bucket);
        var end = Align(normalizedTo, bucket);
        var totals = new Dictionary<DateTime, DysonUsageTimeSeriesPoint>();

        foreach (var row in rows)
        {
            var occurredUtc = NormalizeUtc(row.OccurredUtc);
            if (occurredUtc < normalizedFrom || occurredUtc > normalizedTo)
                continue;

            var bucketStart = Align(occurredUtc, bucket);
            if (!totals.TryGetValue(bucketStart, out var total))
                total = new DysonUsageTimeSeriesPoint { StartUtc = bucketStart };

            totals[bucketStart] = new DysonUsageTimeSeriesPoint
            {
                StartUtc = bucketStart,
                InputTokensAfterCache = total.InputTokensAfterCache + row.InputTokensAfterCache,
                CacheTokens = total.CacheTokens + row.CacheTokens,
                WriteTokensAfterCache = total.WriteTokensAfterCache + row.WriteTokensAfterCache,
            };
        }

        var points = new List<DysonUsageTimeSeriesPoint>();
        for (var bucketStart = start; bucketStart <= end; bucketStart = AddBucket(bucketStart, bucket))
        {
            points.Add(totals.TryGetValue(bucketStart, out var total)
                ? total
                : new DysonUsageTimeSeriesPoint { StartUtc = bucketStart });
        }

        return Result<DysonUsageTimeSeries, string>.AsValue(
            new DysonUsageTimeSeries(normalizedFrom, normalizedTo, bucket, points));
    }

    /// <summary>
    /// Selects the required granularity for the supplied analytics window choice.
    /// Custom windows through two days are hourly; longer custom windows are daily.
    /// </summary>
    public static Result<DysonUsageTimeSeriesBucket, string> ResolveBucket(
        DysonUsageTimeSeriesWindow window,
        DateTime fromUtc,
        DateTime toUtc)
    {
        if (!Enum.IsDefined(window))
            return Result<DysonUsageTimeSeriesBucket, string>.AsError("Unknown usage time-series window.");

        var normalizedFrom = NormalizeUtc(fromUtc);
        var normalizedTo = NormalizeUtc(toUtc);
        if (normalizedFrom > normalizedTo)
            return Result<DysonUsageTimeSeriesBucket, string>.AsError(
                "Usage time-series fromUtc must be on or before toUtc.");

        var bucket = window switch
        {
            DysonUsageTimeSeriesWindow.OneDay => DysonUsageTimeSeriesBucket.Hour,
            DysonUsageTimeSeriesWindow.SevenDays => DysonUsageTimeSeriesBucket.Day,
            DysonUsageTimeSeriesWindow.ThirtyDays => DysonUsageTimeSeriesBucket.Day,
            DysonUsageTimeSeriesWindow.Custom when normalizedTo - normalizedFrom <= TimeSpan.FromDays(2) =>
                DysonUsageTimeSeriesBucket.Hour,
            DysonUsageTimeSeriesWindow.Custom => DysonUsageTimeSeriesBucket.Day,
            _ => throw new InvalidOperationException("Validated usage time-series window was not handled."),
        };

        return Result<DysonUsageTimeSeriesBucket, string>.AsValue(bucket);
    }

    /// <summary>
    /// Produces distinct selector values from unfiltered rows in the selected window. Call this
    /// before applying a selected work-directory or model filter to chart rows.
    /// </summary>
    public static DysonUsageTimeSeriesFilterOptions GetFilterOptions(
        IEnumerable<DysonUsageRequestEntity> unfilteredWindowRows)
    {
        ArgumentNullException.ThrowIfNull(unfilteredWindowRows);

        var rows = unfilteredWindowRows as IReadOnlyCollection<DysonUsageRequestEntity>
            ?? [.. unfilteredWindowRows];
        var workDirectoryNames = rows
            .Select(r => r.WorkDirectoryName ?? "")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var models = rows
            .GroupBy(r => r.ModelSlug ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group.OrderByDescending(r => r.OccurredUtc).First();
                return new DysonUsageTimeSeriesModelOption
                {
                    ModelSlug = group.Key,
                    ModelDisplayAlias = string.IsNullOrWhiteSpace(latest.ModelDisplayAlias)
                        ? group.Key
                        : latest.ModelDisplayAlias,
                };
            })
            .OrderBy(model => model.ModelDisplayAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.ModelSlug, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DysonUsageTimeSeriesFilterOptions
        {
            WorkDirectoryNames = workDirectoryNames,
            Models = models,
        };
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    private static DateTime Align(DateTime value, DysonUsageTimeSeriesBucket bucket) => bucket switch
    {
        DysonUsageTimeSeriesBucket.Hour => new DateTime(
            value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc),
        DysonUsageTimeSeriesBucket.Day => new DateTime(
            value.Year, value.Month, value.Day, 0, 0, 0, DateTimeKind.Utc),
        _ => throw new InvalidOperationException("Validated usage time-series bucket was not handled."),
    };

    private static DateTime AddBucket(DateTime value, DysonUsageTimeSeriesBucket bucket) => bucket switch
    {
        DysonUsageTimeSeriesBucket.Hour => value.AddHours(1),
        DysonUsageTimeSeriesBucket.Day => value.AddDays(1),
        _ => throw new InvalidOperationException("Validated usage time-series bucket was not handled."),
    };
}

/// <summary>Supported analytics window choices.</summary>
public enum DysonUsageTimeSeriesWindow
{
    OneDay,
    SevenDays,
    ThirtyDays,
    Custom,
}

/// <summary>UTC chart bucketing granularity.</summary>
public enum DysonUsageTimeSeriesBucket
{
    Hour,
    Day,
}

/// <summary>One UTC-aligned usage chart point.</summary>
public sealed class DysonUsageTimeSeriesPoint
{
    public DateTime StartUtc { get; init; }
    public int InputTokensAfterCache { get; init; }
    public int CacheTokens { get; init; }
    public int WriteTokensAfterCache { get; init; }
}

/// <summary>Distinct filter values derived from unfiltered usage rows in a selected window.</summary>
public sealed class DysonUsageTimeSeriesFilterOptions
{
    public IReadOnlyList<string> WorkDirectoryNames { get; init; } = [];
    public IReadOnlyList<DysonUsageTimeSeriesModelOption> Models { get; init; } = [];
}

/// <summary>A model selector value retaining both the API slug and its display alias.</summary>
public sealed class DysonUsageTimeSeriesModelOption
{
    public string ModelSlug { get; init; } = "";
    public string ModelDisplayAlias { get; init; } = "";
}
