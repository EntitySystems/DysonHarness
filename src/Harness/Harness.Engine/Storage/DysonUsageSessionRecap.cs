namespace DysonHarness;

/// <summary>
/// Usage-rail recap: token sums nested Total → model → reasoning effort.
/// </summary>
public sealed class DysonUsageSessionRecap
{
    public IReadOnlyList<DysonUsageSessionRecapModel> Models { get; }

    public DysonUsageSessionRecapRow Totals { get; }

    private DysonUsageSessionRecap(
        IReadOnlyList<DysonUsageSessionRecapModel> models,
        DysonUsageSessionRecapRow totals)
    {
        Models = models;
        Totals = totals;
    }

    public static DysonUsageSessionRecap FromRows(IEnumerable<DysonUsageRequestEntity> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var models = rows
            .GroupBy(r => r.ModelSlug ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(static g =>
            {
                var efforts = g
                    .GroupBy(r => r.ReasoningEffort ?? "")
                    .Select(eg =>
                    {
                        var latest = eg.OrderByDescending(r => r.OccurredUtc).First();
                        return SumEntities(
                            eg,
                            g.Key,
                            string.IsNullOrWhiteSpace(latest.ModelDisplayAlias) ? g.Key : latest.ModelDisplayAlias,
                            eg.Key);
                    })
                    .OrderBy(r => string.IsNullOrEmpty(r.ReasoningEffort) ? 1 : 0)
                    .ThenBy(r => r.ReasoningEffort, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var latestModel = g.OrderByDescending(r => r.OccurredUtc).First();
                var alias = string.IsNullOrWhiteSpace(latestModel.ModelDisplayAlias)
                    ? g.Key
                    : latestModel.ModelDisplayAlias;
                return new DysonUsageSessionRecapModel
                {
                    Totals = SumRows(efforts, g.Key, alias, ""),
                    Efforts = efforts,
                };
            })
            .OrderBy(m => m.Totals.ModelSlug, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DysonUsageSessionRecap(models, SumRows(models.Select(m => m.Totals), "", "Total", ""));
    }

    private static DysonUsageSessionRecapRow SumEntities(
        IEnumerable<DysonUsageRequestEntity> rows,
        string modelSlug,
        string modelDisplayAlias,
        string reasoningEffort)
    {
        var list = rows as IReadOnlyCollection<DysonUsageRequestEntity> ?? [.. rows];
        return new DysonUsageSessionRecapRow
        {
            ModelSlug = modelSlug,
            ModelDisplayAlias = modelDisplayAlias,
            ReasoningEffort = reasoningEffort,
            RequestCount = list.Count,
            InputTokens = list.Sum(r => r.InputTokens),
            CacheTokens = list.Sum(r => r.CacheTokens),
            WriteTokens = list.Sum(r => r.WriteTokens),
            CacheWriteTokens = list.Sum(r => r.CacheWriteTokens),
            InputTokensAfterCache = list.Sum(r => r.InputTokensAfterCache),
            WriteTokensAfterCache = list.Sum(r => r.WriteTokensAfterCache),
        };
    }

    private static DysonUsageSessionRecapRow SumRows(
        IEnumerable<DysonUsageSessionRecapRow> rows,
        string modelSlug,
        string modelDisplayAlias,
        string reasoningEffort)
    {
        var list = rows as IReadOnlyList<DysonUsageSessionRecapRow> ?? [.. rows];
        return new DysonUsageSessionRecapRow
        {
            ModelSlug = modelSlug,
            ModelDisplayAlias = modelDisplayAlias,
            ReasoningEffort = reasoningEffort,
            RequestCount = list.Sum(r => r.RequestCount),
            InputTokens = list.Sum(r => r.InputTokens),
            CacheTokens = list.Sum(r => r.CacheTokens),
            WriteTokens = list.Sum(r => r.WriteTokens),
            CacheWriteTokens = list.Sum(r => r.CacheWriteTokens),
            InputTokensAfterCache = list.Sum(r => r.InputTokensAfterCache),
            WriteTokensAfterCache = list.Sum(r => r.WriteTokensAfterCache),
        };
    }
}

public sealed class DysonUsageSessionRecapModel
{
    public DysonUsageSessionRecapRow Totals { get; init; } = new();
    public IReadOnlyList<DysonUsageSessionRecapRow> Efforts { get; init; } = [];
}

public sealed class DysonUsageSessionRecapRow
{
    public string ModelSlug { get; init; } = "";
    public string ModelDisplayAlias { get; init; } = "";
    public string ReasoningEffort { get; init; } = "";
    public int RequestCount { get; init; }
    public int InputTokens { get; init; }
    public int CacheTokens { get; init; }
    public int WriteTokens { get; init; }
    public int CacheWriteTokens { get; init; }
    public int InputTokensAfterCache { get; init; }
    public int WriteTokensAfterCache { get; init; }
}
