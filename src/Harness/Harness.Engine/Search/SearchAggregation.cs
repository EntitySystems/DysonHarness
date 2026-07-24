namespace DysonHarness;

/// <summary>URL dedup, low-quality filter, and simple 1–3 confidence scoring.</summary>
public static class SearchAggregation
{
    public static string NormalizeUrl(string url)
    {
        try
        {
            var u = new Uri(url);
            return $"{u.Host}{u.AbsolutePath.TrimEnd('/')}".ToLowerInvariant();
        }
        catch
        {
            return url.ToLowerInvariant();
        }
    }

    public static IReadOnlyList<SearchHit> FilterLowQuality(IEnumerable<SearchHit> results) =>
        results.Where(r =>
        {
            if (!r.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return false;

            // Keep http(s) hits with a real title even when OpenSearch snippets are empty/short.
            var weakSnippet = string.IsNullOrWhiteSpace(r.Snippet) || r.Snippet.Length < 10;
            if (weakSnippet && string.IsNullOrWhiteSpace(r.Title))
                return false;

            if (r.Url.Contains("y.js?", StringComparison.OrdinalIgnoreCase)
                || r.Url.Contains("/ad/", StringComparison.OrdinalIgnoreCase)
                || r.Url.Contains("duckduckgo.com/y.js", StringComparison.OrdinalIgnoreCase)
                || r.Url.Contains("sogou.com/link", StringComparison.OrdinalIgnoreCase)
                || r.Url.Contains("wikipedia.org/wiki/Category:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }).ToList();

    public static (List<SearchHit> Results, Dictionary<string, int> Frequencies) DedupByUrl(
        IEnumerable<SearchHit> results)
    {
        var seen = new Dictionary<string, SearchHit>(StringComparer.Ordinal);
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var r in results)
        {
            var key = NormalizeUrl(r.Url);
            frequencies[key] = frequencies.GetValueOrDefault(key) + 1;

            if (!seen.TryGetValue(key, out var existing))
            {
                seen[key] = CloneWithEngines(r, r.Engines);
                continue;
            }

            var mergedEngines = existing.Engines
                .Concat(r.Engines)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var keep = (r.Snippet?.Length ?? 0) > (existing.Snippet?.Length ?? 0) ? r : existing;
            seen[key] = CloneWithEngines(keep, mergedEngines);
        }

        return (seen.Values.ToList(), frequencies);
    }

    public static List<SearchHit> DedupByTitle(IEnumerable<SearchHit> results, double threshold = 0.85)
    {
        var kept = new List<SearchHit>();
        foreach (var r in results)
        {
            if (kept.Any(k => Jaccard(k.Title, r.Title) > threshold))
                continue;
            kept.Add(r);
        }

        return kept;
    }

    public static List<SearchHit> ScoreAndRank(
        IEnumerable<SearchHit> results,
        string query,
        Dictionary<string, int>? frequencies = null)
    {
        var tokens = query.ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n', '-', '_', '.', ',', ':', '/', '?'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3)
            .ToArray();

        var scored = results.Select(r =>
        {
            var key = NormalizeUrl(r.Url);
            var freq = frequencies?.GetValueOrDefault(key) ?? Math.Max(1, r.Engines.Count);
            var confidence = Math.Clamp(Math.Max(freq, r.Engines.Count), 1, 3);
            var score = CalculateScore(r, tokens, freq);
            return CloneScored(r, confidence, score);
        }).ToList();

        scored.Sort((a, b) =>
        {
            var c = b.Confidence.CompareTo(a.Confidence);
            return c != 0 ? c : b.Score.CompareTo(a.Score);
        });
        return scored;
    }

    public static List<SearchHit> ApplyDomainFilters(
        IEnumerable<SearchHit> results,
        IReadOnlyList<string>? includeDomains,
        IReadOnlyList<string>? excludeDomains)
    {
        IEnumerable<SearchHit> q = results;
        if (includeDomains is { Count: > 0 })
        {
            q = q.Where(r =>
            {
                try
                {
                    var host = new Uri(r.Url).Host;
                    return includeDomains.Any(d =>
                        host.Contains(d, StringComparison.OrdinalIgnoreCase)
                        || host.EndsWith(d, StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    return false;
                }
            });
        }

        if (excludeDomains is { Count: > 0 })
        {
            q = q.Where(r =>
            {
                try
                {
                    var host = new Uri(r.Url).Host;
                    return !excludeDomains.Any(d =>
                        host.Contains(d, StringComparison.OrdinalIgnoreCase)
                        || host.EndsWith(d, StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    return true;
                }
            });
        }

        return q.ToList();
    }

    public static bool ConfidenceBasketSufficient(
        IReadOnlyList<SearchHit> results,
        int minResults = 3,
        double minAvgConfidence = 0.6,
        int topK = 5)
    {
        if (results.Count == 0)
            return false;

        var top = results.OrderByDescending(r => r.Confidence).ThenByDescending(r => r.Score).Take(topK).ToList();
        if (top.Count < minResults)
            return false;

        var avg = top.Average(r => r.Confidence / 3.0);
        return avg >= minAvgConfidence;
    }

    private static double CalculateScore(SearchHit result, string[] tokens, int frequency)
    {
        if (tokens.Length == 0)
            return 0.3;

        var titleLower = result.Title.ToLowerInvariant();
        var bodyLower = (result.Snippet ?? "").ToLowerInvariant();
        var hasTitle = tokens.Any(t => titleLower.Contains(t, StringComparison.Ordinal));
        var hasBody = tokens.Any(t => bodyLower.Contains(t, StringComparison.Ordinal));

        double bucket = (hasTitle, hasBody) switch
        {
            (true, true) => 0.4,
            (true, false) => 0.3,
            (false, true) => 0.2,
            _ => 0,
        };

        try
        {
            var host = new Uri(result.Url).Host.ToLowerInvariant();
            if (host.EndsWith("wikipedia.org", StringComparison.Ordinal)) bucket += 0.15;
            else if (host.EndsWith("github.com", StringComparison.Ordinal)) bucket += 0.08;
            else if (host.EndsWith(".edu", StringComparison.Ordinal) || host.EndsWith(".gov", StringComparison.Ordinal))
                bucket += 0.12;
        }
        catch
        {
            // ignore bad urls
        }

        var freqBonus = Math.Min(frequency * 0.1, 0.3);
        return Math.Min(0.1 + bucket + freqBonus, 1.0);
    }

    private static double Jaccard(string a, string b)
    {
        var setA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var setB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (setA.Count == 0 && setB.Count == 0)
            return 1;
        var intersection = setA.Count(x => setB.Contains(x));
        var union = setA.Count + setB.Count - intersection;
        return union > 0 ? (double)intersection / union : 0;
    }

    private static SearchHit CloneWithEngines(SearchHit r, IEnumerable<string> engines) =>
        new()
        {
            Title = r.Title,
            Url = r.Url,
            Snippet = r.Snippet,
            Source = r.Source,
            Engines = engines.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Confidence = r.Confidence,
            Score = r.Score,
        };

    private static SearchHit CloneScored(SearchHit r, int confidence, double score) =>
        new()
        {
            Title = r.Title,
            Url = r.Url,
            Snippet = r.Snippet,
            Source = r.Source,
            Engines = r.Engines.ToList(),
            Confidence = confidence,
            Score = score,
        };
}
