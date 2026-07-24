using System.Text;
using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// In-process search orchestrator: parallel FreeSearch and waterfall FreeSearchAdvanced.
/// ponytail: free order is DDG HTML → Bing RSS → Wikipedia; Brave optional when keyed.
/// </summary>
public static class SearchOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string ResolveBraveApiKey(DysonAgentSessionConfig? config)
    {
        if (!string.IsNullOrWhiteSpace(config?.BraveApiKey))
            return config.BraveApiKey.Trim();
        return Environment.GetEnvironmentVariable("BRAVE_API_KEY")?.Trim() ?? "";
    }

    public static async Task<Result<SearchResponse, string>> FreeSearchAsync(
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Query))
            return Result<SearchResponse, string>.AsError("query is required.");

        var count = Math.Clamp(options.Count <= 0 ? 10 : options.Count, 1, 20);
        var braveKey = options.BraveApiKey ?? "";
        var engines = ResolveEngines(options.Engines, braveKey, waterfall: false);

        var (hits, failures, used) = await RunEnginesAsync(
            engines, options.Query, count, braveKey, cancellationToken).ConfigureAwait(false);

        var response = BuildResponse(options.Query, hits, used, failures, waterfall: false, phasesRun: 1, count, options);
        return Result<SearchResponse, string>.AsValue(response);
    }

    public static async Task<Result<SearchResponse, string>> FreeSearchAdvancedAsync(
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Query))
            return Result<SearchResponse, string>.AsError("query is required.");

        var count = Math.Clamp(options.Count <= 0 ? 5 : options.Count, 1, 20);
        options = CloneWithCount(options, count);

        if (!options.Waterfall)
            return await FreeSearchAsync(options, cancellationToken).ConfigureAwait(false);

        var braveKey = options.BraveApiKey ?? "";
        var allHits = new List<SearchHit>();
        var failures = new List<string>();
        var used = new List<string>();
        var phases = 0;

        // Phase 1: free engines (DDG first, then Bing RSS, then Wikipedia)
        phases++;
        var phase1 = await RunEnginesAsync(
            SearchEngineIds.FreeDefault,
            options.Query, count, braveKey, cancellationToken).ConfigureAwait(false);
        allHits.AddRange(phase1.Hits);
        failures.AddRange(phase1.Failures);
        used.AddRange(phase1.Used);

        var scored = Aggregate(allHits, options);
        var sufficient = SearchAggregation.ConfidenceBasketSufficient(
            scored, options.WaterfallMinResults, options.WaterfallMinConfidence);

        // Phase 2: Brave when key present
        if (!sufficient && !string.IsNullOrWhiteSpace(braveKey))
        {
            phases++;
            var phase2 = await RunEnginesAsync(
                [SearchEngineIds.Brave],
                options.Query, count, braveKey, cancellationToken).ConfigureAwait(false);
            allHits.AddRange(phase2.Hits);
            failures.AddRange(phase2.Failures);
            used.AddRange(phase2.Used);
            scored = Aggregate(allHits, options);
        }

        if (options.Enrich)
            scored = await EnrichAsync(scored, options.EnrichMax, cancellationToken).ConfigureAwait(false);

        var response = Finalize(options.Query, scored, used.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            failures, waterfall: true, phases, count, options);
        return Result<SearchResponse, string>.AsValue(response);
    }

    public static async Task<Result<SearchResponse, string>> SearchWithSynthesisAsync(
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        var advancedOptions = CloneWithCount(options, options.Count <= 0 ? 10 : options.Count);
        advancedOptions.Waterfall = true;
        advancedOptions.Enrich = true;

        var advanced = await FreeSearchAdvancedAsync(advancedOptions, cancellationToken).ConfigureAwait(false);
        if (advanced.IsError)
            return advanced;

        var response = advanced.Value;
        var hint = BuildPromptHint(response.Query, response.Results);
        return Result<SearchResponse, string>.AsValue(new SearchResponse
        {
            Query = response.Query,
            Results = response.Results,
            Meta = response.Meta,
            PromptHint = hint,
        });
    }

    public static string ToJson(SearchResponse response)
    {
        var payload = new
        {
            query = response.Query,
            results = response.Results.Select(r => new
            {
                title = r.Title,
                url = r.Url,
                snippet = r.Snippet,
                confidence = r.Confidence,
                source = r.Source,
                engines = r.Engines,
            }),
            meta = new
            {
                total = response.Meta.Total,
                high_confidence = response.Meta.HighConfidence,
                engines = response.Meta.Engines,
                waterfall = response.Meta.Waterfall,
                phases_run = response.Meta.PhasesRun,
                partial_failures = response.Meta.PartialFailures,
            },
            prompt_hint = response.PromptHint,
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static string BuildPromptHint(string query, IReadOnlyList<SearchHit> results)
    {
        if (results.Count == 0)
            return $"No search results found for query: \"{query}\"";

        var sb = new StringBuilder();
        sb.Append("You are analyzing search results for the query: \"").Append(query).Append("\"\n\n");
        sb.AppendLine("Here are the results from multiple search engines (sorted by confidence):\n");

        var top = results.Take(10).ToList();
        for (var i = 0; i < top.Count; i++)
        {
            var r = top[i];
            sb.Append('[').Append(i + 1).Append("] ").AppendLine(r.Title);
            sb.Append("    URL: ").AppendLine(r.Url);
            sb.Append("    Source: ").Append(r.Source).Append(", Confidence: ").Append(r.Confidence).Append("/3\n");
            if (!string.IsNullOrEmpty(r.Snippet))
            {
                var snip = r.Snippet.Length > 300 ? r.Snippet[..300] : r.Snippet;
                sb.Append("    Snippet: ").AppendLine(snip);
            }

            sb.AppendLine();
        }

        sb.AppendLine("Note on confidence scores: 1 = single source, 2 = verified by 2+ engines, 3 = verified by 3+ engines.");
        sb.Append("Based on these results, please provide a concise, factual answer with citations using [1], [2], etc. ");
        sb.Append("If results are insufficient, contradictory, or lack authoritative sources, note that honestly.");
        return sb.ToString();
    }

    private static List<string> ResolveEngines(
        IReadOnlyList<string>? requested,
        string braveKey,
        bool waterfall)
    {
        IEnumerable<string> engines;
        if (requested is { Count: > 0 })
        {
            engines = requested
                .Select(e => e.Trim().ToLowerInvariant())
                .Where(e => SearchEngineIds.AllKnown.Contains(e, StringComparer.Ordinal));
        }
        else
        {
            engines = SearchEngineIds.FreeDefault;
            if (!string.IsNullOrWhiteSpace(braveKey) && !waterfall)
                engines = engines.Append(SearchEngineIds.Brave);
        }

        return engines
            .Where(e => e != SearchEngineIds.Brave || !string.IsNullOrWhiteSpace(braveKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<(List<SearchHit> Hits, List<string> Failures, List<string> Used)> RunEnginesAsync(
        IReadOnlyList<string> engines,
        string query,
        int count,
        string braveKey,
        CancellationToken cancellationToken)
    {
        var hits = new List<SearchHit>();
        var failures = new List<string>();
        var used = new List<string>();

        // Preserve declared engine order in meta / aggregation (DDG hits first when FreeDefault).
        var tasks = engines.Select(async engine =>
        {
            try
            {
                var result = await SearchEngines.SearchAsync(engine, query, count, braveKey, cancellationToken)
                    .ConfigureAwait(false);
                if (result.IsError)
                    return (engine, results: (IReadOnlyList<SearchHit>)[], error: $"{engine}: {result.Error}");
                return (engine, results: result.Value, error: (string?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (engine, results: (IReadOnlyList<SearchHit>)[], error: $"{engine}: {ex.Message}");
            }
        }).ToArray();

        var settled = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var (engine, results, error) in settled)
        {
            used.Add(engine);
            if (error is not null)
                failures.Add(error);
            else if (results.Count == 0)
                failures.Add($"{engine}: no results");
            else
                hits.AddRange(results);
        }

        return (hits, failures, used);
    }

    private static List<SearchHit> Aggregate(IEnumerable<SearchHit> hits, SearchOptions options)
    {
        var filtered = SearchAggregation.FilterLowQuality(hits);
        var (deduped, frequencies) = SearchAggregation.DedupByUrl(filtered);
        var titled = SearchAggregation.DedupByTitle(deduped);
        var scored = SearchAggregation.ScoreAndRank(titled, options.Query, frequencies);
        scored = SearchAggregation.ApplyDomainFilters(scored, options.IncludeDomains, options.ExcludeDomains);
        if (options.MinConfidence > 1)
            scored = scored.Where(r => r.Confidence >= options.MinConfidence).ToList();
        return scored;
    }

    private static SearchResponse BuildResponse(
        string query,
        List<SearchHit> hits,
        List<string> used,
        List<string> failures,
        bool waterfall,
        int phasesRun,
        int count,
        SearchOptions options)
    {
        var scored = Aggregate(hits, options);
        return Finalize(query, scored, used, failures, waterfall, phasesRun, count, options);
    }

    private static SearchResponse Finalize(
        string query,
        List<SearchHit> scored,
        List<string> used,
        List<string> failures,
        bool waterfall,
        int phasesRun,
        int count,
        SearchOptions options)
    {
        var limited = scored.Take(count).ToList();
        return new SearchResponse
        {
            Query = query,
            Results = limited,
            Meta = new SearchRunMeta
            {
                Total = limited.Count,
                HighConfidence = limited.Count(r => r.Confidence >= 2),
                Engines = used,
                PartialFailures = failures.Count > 0 ? failures : null,
                Waterfall = waterfall,
                PhasesRun = phasesRun,
            },
        };
    }

    private static async Task<List<SearchHit>> EnrichAsync(
        List<SearchHit> results,
        int enrichMax,
        CancellationToken cancellationToken)
    {
        // ponytail: enrich only low-confidence tops via Jina; skip full-page crawl matrix
        var toEnrich = results
            .Where(r => r.Confidence <= 1)
            .Take(Math.Clamp(enrichMax, 1, 10))
            .ToList();

        foreach (var hit in toEnrich)
        {
            var extracted = await SearchFetch.FreeExtractAsync(hit.Url, maxLength: 800, cancellationToken)
                .ConfigureAwait(false);
            if (extracted.IsSuccess && !string.IsNullOrWhiteSpace(extracted.Value))
                hit.Snippet = extracted.Value.Trim();
        }

        return results;
    }

    private static SearchOptions CloneWithCount(SearchOptions options, int count) =>
        new()
        {
            Query = options.Query,
            Count = count,
            Engines = options.Engines,
            MinConfidence = options.MinConfidence,
            IncludeDomains = options.IncludeDomains,
            ExcludeDomains = options.ExcludeDomains,
            Waterfall = options.Waterfall,
            WaterfallMinResults = options.WaterfallMinResults,
            WaterfallMinConfidence = options.WaterfallMinConfidence,
            Enrich = options.Enrich,
            EnrichMax = options.EnrichMax,
            BraveApiKey = options.BraveApiKey,
        };
}
