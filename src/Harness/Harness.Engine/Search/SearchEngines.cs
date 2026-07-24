using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>
/// Free search engines: DuckDuckGo HTML (default first), Bing RSS fallback, Wikipedia OpenSearch, optional Brave.
/// ponytail: DDG/Bing HTML selectors drift — prefer RSS/API; add a managed SERP API if both break.
/// </summary>
public static class SearchEngines
{
    public static async Task<Result<IReadOnlyList<SearchHit>, string>> SearchAsync(
        string engineId,
        string query,
        int limit,
        string? braveApiKey,
        CancellationToken cancellationToken)
    {
        return engineId.ToLowerInvariant() switch
        {
            SearchEngineIds.DuckDuckGo => await SearchDuckDuckGoAsync(query, limit, cancellationToken)
                .ConfigureAwait(false),
            SearchEngineIds.Bing => await SearchBingAsync(query, limit, cancellationToken).ConfigureAwait(false),
            SearchEngineIds.Wikipedia => await SearchWikipediaAsync(query, limit, cancellationToken)
                .ConfigureAwait(false),
            SearchEngineIds.Brave => await SearchBraveAsync(query, limit, braveApiKey, cancellationToken)
                .ConfigureAwait(false),
            _ => Result<IReadOnlyList<SearchHit>, string>.AsError($"unknown engine '{engineId}'"),
        };
    }

    public static async Task<Result<IReadOnlyList<SearchHit>, string>> SearchDuckDuckGoAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            using var res = await SearchHttp.Client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                return Result<IReadOnlyList<SearchHit>, string>.AsError($"HTTP {(int)res.StatusCode}");

            var html = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = ParseDuckDuckGoHtml(html, limit);
            if (parsed.Count == 0
                && (html.Contains("captcha", StringComparison.OrdinalIgnoreCase)
                    || html.Contains("challenge", StringComparison.OrdinalIgnoreCase)))
            {
                return Result<IReadOnlyList<SearchHit>, string>.AsError("blocked or unparseable SERP");
            }

            return Result<IReadOnlyList<SearchHit>, string>.AsValue(parsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<SearchHit>, string>.AsError(ex.Message);
        }
    }

    internal static List<SearchHit> ParseDuckDuckGoHtml(string html, int limit)
    {
        var results = new List<SearchHit>();
        // Each organic block: result__a (title/href) then optional result__snippet
        var blockRegex = new Regex(
            """<a[^>]*class="[^"]*result__a[^"]*"[^>]*href="([^"]+)"[^>]*>([\s\S]*?)</a>([\s\S]*?)(?=<a[^>]*class="[^"]*result__a|</div>\s*</div>\s*</div>|$)""",
            RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var snippetRegex = new Regex(
            """class="[^"]*result__snippet[^"]*"[^>]*>([\s\S]*?)</(?:a|td|span|div)>""",
            RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

        foreach (Match match in blockRegex.Matches(html))
        {
            if (results.Count >= limit)
                break;

            var href = WebUtility.HtmlDecode(match.Groups[1].Value);
            var title = DecodeHtml(match.Groups[2].Value);
            var snippet = "";
            var snipMatch = snippetRegex.Match(match.Groups[3].Value);
            if (snipMatch.Success)
                snippet = DecodeHtml(snipMatch.Groups[1].Value);

            var link = ResolveDuckDuckGoUrl(href);
            if (string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(title))
                continue;

            results.Add(new SearchHit
            {
                Title = title,
                Url = link,
                Snippet = snippet,
                Source = SearchEngineIds.DuckDuckGo,
                Engines = [SearchEngineIds.DuckDuckGo],
            });
        }

        return results;
    }

    internal static string ResolveDuckDuckGoUrl(string href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return "";

        var decoded = WebUtility.HtmlDecode(href.Trim());
        if (decoded.StartsWith("//", StringComparison.Ordinal))
            decoded = "https:" + decoded;

        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var uri))
            return decoded;

        // DDG wraps destinations as /l/?uddg=<urlencoded url>
        var uddg = ExtractQueryParam(uri.Query, "uddg");
        if (!string.IsNullOrWhiteSpace(uddg))
            return Uri.UnescapeDataString(uddg);

        return uri.ToString();
    }

    private static string? ExtractQueryParam(string query, string name)
    {
        if (string.IsNullOrEmpty(query))
            return null;

        var trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = eq < 0 ? part : part[..eq];
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            return eq < 0 ? "" : part[(eq + 1)..];
        }

        return null;
    }

    public static async Task<Result<IReadOnlyList<SearchHit>, string>> SearchBingAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}&count={limit}&format=rss";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/rss+xml, application/xml, text/xml, */*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            using var res = await SearchHttp.Client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                return Result<IReadOnlyList<SearchHit>, string>.AsError($"HTTP {(int)res.StatusCode}");

            var body = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = ParseBingRss(body, limit);
            if (parsed.Count == 0)
            {
                var looksChallenge = body.Contains("captcha", StringComparison.OrdinalIgnoreCase)
                    || body.Contains("challenge", StringComparison.OrdinalIgnoreCase);
                var hasItem = body.Contains("<item", StringComparison.OrdinalIgnoreCase);
                if (looksChallenge && !hasItem)
                    return Result<IReadOnlyList<SearchHit>, string>.AsError("blocked or unparseable SERP");
            }

            return Result<IReadOnlyList<SearchHit>, string>.AsValue(parsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<SearchHit>, string>.AsError(ex.Message);
        }
    }

    internal static List<SearchHit> ParseBingRss(string xml, int limit)
    {
        var results = new List<SearchHit>();
        var itemRegex = new Regex(
            """<item>([\s\S]*?)</item>""",
            RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

        foreach (Match item in itemRegex.Matches(xml))
        {
            if (results.Count >= limit)
                break;

            var block = item.Groups[1].Value;
            var title = ExtractXmlTag(block, "title");
            var link = ExtractXmlTag(block, "link");
            var description = ExtractXmlTag(block, "description");
            if (string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(title))
                continue;

            results.Add(new SearchHit
            {
                Title = DecodeHtml(StripCdata(title)),
                Url = StripCdata(link).Trim(),
                Snippet = DecodeHtml(StripCdata(description ?? "")),
                Source = SearchEngineIds.Bing,
                Engines = [SearchEngineIds.Bing],
            });
        }

        return results;
    }

    public static async Task<Result<IReadOnlyList<SearchHit>, string>> SearchWikipediaAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            var maxLimit = Math.Min(limit, 10);
            var url =
                "https://en.wikipedia.org/w/api.php?action=opensearch&profile=fuzzy" +
                $"&limit={maxLimit}&search={Uri.EscapeDataString(query)}&format=json&origin=*";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var res = await SearchHttp.Client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                return Result<IReadOnlyList<SearchHit>, string>.AsError($"HTTP {(int)res.StatusCode}");

            await using var stream = await res.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() < 4)
                return Result<IReadOnlyList<SearchHit>, string>.AsError("unparseable OpenSearch response");

            var titles = doc.RootElement[1];
            var snippets = doc.RootElement[2];
            var urls = doc.RootElement[3];
            var results = new List<SearchHit>();

            for (var i = 0; i < titles.GetArrayLength() && results.Count < limit; i++)
            {
                var title = titles[i].GetString();
                var link = urls[i].GetString();
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link))
                    continue;

                var snippet = i < snippets.GetArrayLength() ? snippets[i].GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(snippet))
                    snippet = title;

                results.Add(new SearchHit
                {
                    Title = title,
                    Url = link,
                    Snippet = snippet,
                    Source = SearchEngineIds.Wikipedia,
                    Engines = [SearchEngineIds.Wikipedia],
                });
            }

            return Result<IReadOnlyList<SearchHit>, string>.AsValue(results);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<SearchHit>, string>.AsError(ex.Message);
        }
    }

    public static async Task<Result<IReadOnlyList<SearchHit>, string>> SearchBraveAsync(
        string query,
        int limit,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return Result<IReadOnlyList<SearchHit>, string>.AsError("missing API key");

        try
        {
            var url =
                "https://api.search.brave.com/res/v1/web/search" +
                $"?q={Uri.EscapeDataString(query)}&count={limit}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("X-Subscription-Token", apiKey.Trim());

            using var res = await SearchHttp.Client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                return Result<IReadOnlyList<SearchHit>, string>.AsError($"HTTP {(int)res.StatusCode}");

            await using var stream = await res.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("web", out var web)
                || !web.TryGetProperty("results", out var webResults)
                || webResults.ValueKind != JsonValueKind.Array)
            {
                return Result<IReadOnlyList<SearchHit>, string>.AsError("unparseable Brave response");
            }

            var results = new List<SearchHit>();
            foreach (var item in webResults.EnumerateArray())
            {
                if (results.Count >= limit)
                    break;

                var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                var link = item.TryGetProperty("url", out var u) ? u.GetString() : null;
                var snippet = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link))
                    continue;

                results.Add(new SearchHit
                {
                    Title = title,
                    Url = link,
                    Snippet = snippet,
                    Source = SearchEngineIds.Brave,
                    Engines = [SearchEngineIds.Brave],
                });
            }

            return Result<IReadOnlyList<SearchHit>, string>.AsValue(results);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<SearchHit>, string>.AsError(ex.Message);
        }
    }

    private static string? ExtractXmlTag(string block, string tag)
    {
        var match = Regex.Match(
            block,
            $"""<{tag}(?:\s[^>]*)?>([\s\S]*?)</{tag}>""",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string StripCdata(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("<![CDATA[", StringComparison.OrdinalIgnoreCase)
            && trimmed.EndsWith("]]>", StringComparison.Ordinal))
        {
            return trimmed["<![CDATA[".Length..^3];
        }

        return trimmed;
    }

    private static string DecodeHtml(string value)
    {
        var noTags = Regex.Replace(value, "<[^>]+>", " ");
        return WebUtility.HtmlDecode(noTags).Trim();
    }
}
