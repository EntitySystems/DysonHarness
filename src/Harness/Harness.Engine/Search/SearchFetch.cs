using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>WebFetch / FreeExtract / FetchGithubReadme with shared SSRF validation.</summary>
public static class SearchFetch
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<Result<WebFetchResult, string>> WebFetchAsync(
        string url,
        int? maxBytes = null,
        CancellationToken cancellationToken = default)
    {
        var validation = SearchHttp.ValidateUrl(url);
        if (validation.IsError)
            return Result<WebFetchResult, string>.AsError(validation.Error);

        var cap = Math.Clamp(maxBytes ?? 512_000, 1_024, 2_000_000);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url.Trim());
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*;q=0.8");

            using var res = await SearchHttp.Client
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var finalUrl = res.RequestMessage?.RequestUri?.ToString() ?? url.Trim();
            // Re-check final URL after redirects (SSRF)
            var finalCheck = SearchHttp.ValidateUrl(finalUrl);
            if (finalCheck.IsError)
                return Result<WebFetchResult, string>.AsError($"Redirect blocked: {finalCheck.Error}");

            var contentType = res.Content.Headers.ContentType?.ToString();
            await using var stream = await res.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            var truncated = false;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                    break;
                var remaining = cap - (int)ms.Length;
                if (remaining <= 0)
                {
                    truncated = true;
                    break;
                }

                var toWrite = Math.Min(read, remaining);
                ms.Write(buffer, 0, toWrite);
                if (toWrite < read)
                {
                    truncated = true;
                    break;
                }
            }

            var html = Encoding.UTF8.GetString(ms.ToArray());
            return Result<WebFetchResult, string>.AsValue(new WebFetchResult
            {
                StatusCode = (int)res.StatusCode,
                ContentType = contentType,
                FinalUrl = finalUrl,
                Html = html,
                Truncated = truncated,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<WebFetchResult, string>.AsError(ex.Message);
        }
    }

    public static string WebFetchToJson(WebFetchResult result) =>
        JsonSerializer.Serialize(new
        {
            statusCode = result.StatusCode,
            contentType = result.ContentType,
            finalUrl = result.FinalUrl,
            truncated = result.Truncated,
            html = result.Html,
        }, JsonOptions);

    public static async Task<Result<string, string>> FreeExtractAsync(
        string url,
        int maxLength = 5000,
        CancellationToken cancellationToken = default)
    {
        var validation = SearchHttp.ValidateUrl(url);
        if (validation.IsError)
            return Result<string, string>.AsError(validation.Error);

        var cap = Math.Clamp(maxLength <= 0 ? 5000 : maxLength, 100, 100_000);

        try
        {
            var jinaUrl = "https://r.jina.ai/" + url.Trim();
            using var req = new HttpRequestMessage(HttpMethod.Get, jinaUrl);
            req.Headers.TryAddWithoutValidation("Accept", "text/markdown");

            using var res = await SearchHttp.Client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                return Result<string, string>.AsError($"HTTP {(int)res.StatusCode}");

            var content = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (content.Length > cap)
                content = content[..cap];
            return Result<string, string>.AsValue(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError(ex.Message);
        }
    }

    public static async Task<Result<string, string>> FetchGithubReadmeAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var validation = SearchHttp.ValidateUrl(url);
        if (validation.IsError)
            return Result<string, string>.AsError(validation.Error);

        var match = Regex.Match(url, @"github\.com/([^/]+)/([^/#?]+)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return Result<string, string>.AsError("Invalid GitHub URL");

        var owner = match.Groups[1].Value;
        var repo = match.Groups[2].Value.Replace(".git", "", StringComparison.OrdinalIgnoreCase);
        string[] files = ["README.md", "readme.md", "Readme.md", "README.MD", "README"];
        string[] branches = ["main", "master"];

        foreach (var branch in branches)
        {
            foreach (var file in files)
            {
                var raw = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{file}";
                try
                {
                    using var res = await SearchHttp.Client.GetAsync(raw, cancellationToken).ConfigureAwait(false);
                    if (!res.IsSuccessStatusCode)
                        continue;

                    var content = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    return Result<string, string>.AsValue($"# {owner}/{repo}\n\n{content}");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // try next candidate
                }
            }
        }

        return Result<string, string>.AsError("README not found");
    }
}
