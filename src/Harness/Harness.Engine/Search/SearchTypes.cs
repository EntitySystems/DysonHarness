namespace DysonHarness;

public static class SearchEngineIds
{
    public const string DuckDuckGo = "duckduckgo";
    public const string Bing = "bing";
    public const string Wikipedia = "wikipedia";
    public const string Brave = "brave";

    /// <summary>Free engines in waterfall / default FreeSearch order: DDG first, then Bing RSS, then Wikipedia.</summary>
    public static readonly string[] FreeDefault = [DuckDuckGo, Bing, Wikipedia];
    public static readonly string[] AllKnown = [DuckDuckGo, Bing, Wikipedia, Brave];
}

public sealed class SearchHit
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string Snippet { get; set; } = "";
    public required string Source { get; init; }
    public List<string> Engines { get; init; } = [];
    /// <summary>1–3: how many distinct engines returned this URL (capped).</summary>
    public int Confidence { get; set; } = 1;
    public double Score { get; set; }
}

public sealed class SearchRunMeta
{
    public int Total { get; init; }
    public int HighConfidence { get; init; }
    public IReadOnlyList<string> Engines { get; init; } = [];
    public IReadOnlyList<string>? PartialFailures { get; init; }
    public bool Waterfall { get; init; }
    public int PhasesRun { get; init; }
}

public sealed class SearchResponse
{
    public required string Query { get; init; }
    public IReadOnlyList<SearchHit> Results { get; init; } = [];
    public required SearchRunMeta Meta { get; init; }
    public string? PromptHint { get; init; }
}

public sealed class SearchOptions
{
    public required string Query { get; init; }
    public int Count { get; set; } = 10;
    public IReadOnlyList<string>? Engines { get; set; }
    public int MinConfidence { get; set; } = 1;
    public IReadOnlyList<string>? IncludeDomains { get; set; }
    public IReadOnlyList<string>? ExcludeDomains { get; set; }
    public bool Waterfall { get; set; }
    public int WaterfallMinResults { get; set; } = 3;
    public double WaterfallMinConfidence { get; set; } = 0.6;
    public bool Enrich { get; set; }
    public int EnrichMax { get; set; } = 3;
    public string? BraveApiKey { get; set; }
}

public sealed class WebFetchResult
{
    public int StatusCode { get; init; }
    public string? ContentType { get; init; }
    public required string FinalUrl { get; init; }
    public required string Html { get; init; }
    public bool Truncated { get; init; }
}
