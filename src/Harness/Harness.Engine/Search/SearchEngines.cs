using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>
/// MVP search engines: Bing HTML SERP, Wikipedia OpenSearch, optional Brave API.
/// ponytail: Bing HTML selectors break when Bing redesigns — add a managed SERP API later.
/// </summary>
public static class SearchEngines
{
    public static async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string engineId,
        string query,
        int limit,
        string? braveApiKey,
        CancellationToken cancellationToken)
    {
        return engineId.ToLowerInvariant() switch
        {
            SearchEngineIds.Bing => await SearchBingAsync(query, limit, cancellationToken).ConfigureAwait(false),
            SearchEngineIds.Wikipedia => await SearchWikipediaAsync(query, limit, cancellationToken).ConfigureAwait(false),
            SearchEngineIds.Brave => await SearchBraveAsync(query, limit, braveApiKey, cancellationToken).ConfigureAwait(false),
            _ => [],
        };
    }

    public static async Task<IReadOnlyList<SearchHit>> SearchBingAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}&count={limit}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            using var res = await SearchHttp.Client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                return [];

            var html = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseBingHtml(html, limit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    internal static List<SearchHit> ParseBingHtml(string html, int limit)
    {
        var results = new List<SearchHit>();
        var regex = new Regex(
            """<li class="b_algo">[\s\S]*?<h2><a href="([^"]+)"[^>]*>([\s\S]*?)</a></h2>[\s\S]*?<p[^>]*>([\s\S]*?)</p>""",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        foreach (Match match in regex.Matches(html))
        {
            if (results.Count >= limit)
                break;

            var link = match.Groups[1].Value;
            var title = DecodeHtml(match.Groups[2].Value);
            var snippet = DecodeHtml(match.Groups[3].Value);
            if (string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(title))
                continue;

            results.Add(new SearchHit
            {
                Title = title,
                Url = link,
                Snippet = snippet,
                Source = SearchEngineIds.Bing,
                Engines = [SearchEngineIds.Bing],
            });
        }

        return results;
    }

    public static async Task<IReadOnlyList<SearchHit>> SearchWikipediaAsync(
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

            using var res = await SearchHttp.Client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                return [];

            await using var stream = await res.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() < 4)
                return [];

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

            return results;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    public static async Task<IReadOnlyList<SearchHit>> SearchBraveAsync(
        string query,
        int limit,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return [];

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
                return [];

            await using var stream = await res.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("web", out var web)
                || !web.TryGetProperty("results", out var webResults)
                || webResults.ValueKind != JsonValueKind.Array)
            {
                return [];
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

            return results;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private static string DecodeHtml(string value)
    {
        var noTags = Regex.Replace(value, "<[^>]+>", " ");
        return WebUtility.HtmlDecode(noTags).Trim();
    }
}
