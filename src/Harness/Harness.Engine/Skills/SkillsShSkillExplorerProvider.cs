using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DysonHarness;

/// <summary>
/// skills.sh explorer: unauthenticated <c>/api/search</c> + GitHub zipball skill-folder install.
/// </summary>
public sealed class SkillsShSkillExplorerProvider(HttpClient http) : IDysonSkillExplorerProvider
{
    public const string ProviderId = "skillssh";
    public const string ProviderDisplayName = "skills.sh";

    private const string SearchPath = "api/search";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public string ProviderName => ProviderId;

    public string DisplayName => ProviderDisplayName;

    public async Task<Result<DysonSkillExplorerSearchPage, string>> SearchAsync(
        string? query,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1)
            return Result<DysonSkillExplorerSearchPage, string>.AsError("limit must be at least 1.");
        if (offset < 0)
            return Result<DysonSkillExplorerSearchPage, string>.AsError("offset must be >= 0.");

        // Public search is query-oriented; empty q → empty page (no browse endpoint without OIDC).
        if (string.IsNullOrWhiteSpace(query))
        {
            return Result<DysonSkillExplorerSearchPage, string>.AsValue(
                new DysonSkillExplorerSearchPage([], 0, Math.Min(limit, 100), offset, HasMore: false));
        }

        // ponytail: /api/search has no offset — first page only
        if (offset > 0)
        {
            return Result<DysonSkillExplorerSearchPage, string>.AsValue(
                new DysonSkillExplorerSearchPage([], 0, Math.Min(limit, 100), offset, HasMore: false));
        }

        var clampedLimit = Math.Min(limit, 100);
        var url = SearchPath
            + "?q=" + Uri.EscapeDataString(query.Trim())
            + "&limit=" + clampedLimit;

        var json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        if (json.IsError)
            return Result<DysonSkillExplorerSearchPage, string>.AsError(json.Error);

        try
        {
            var dto = JsonSerializer.Deserialize<SearchResponse>(json.Value, JsonOptions);
            if (dto?.Skills is null)
            {
                return Result<DysonSkillExplorerSearchPage, string>.AsError(
                    "skills.sh search returned an unexpected payload.");
            }

            var skills = dto.Skills.Select(MapEntry).ToList();
            return Result<DysonSkillExplorerSearchPage, string>.AsValue(
                new DysonSkillExplorerSearchPage(
                    skills,
                    Total: skills.Count,
                    Limit: clampedLimit,
                    Offset: 0,
                    HasMore: false));
        }
        catch (JsonException ex)
        {
            return Result<DysonSkillExplorerSearchPage, string>.AsError(
                "skills.sh search JSON was invalid: " + ex.Message, ex);
        }
    }

    public async Task<Result<DysonSkillExplorerEntry, string>> GetAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        var parsed = ParseSlug(slug);
        if (parsed.IsError)
            return Result<DysonSkillExplorerEntry, string>.AsError(parsed.Error);

        var page = await SearchAsync(parsed.Value.SkillId, limit: 20, offset: 0, cancellationToken)
            .ConfigureAwait(false);
        if (page.IsError)
            return Result<DysonSkillExplorerEntry, string>.AsError(page.Error);

        var match = page.Value.Skills.FirstOrDefault(s =>
            string.Equals(s.Slug, parsed.Value.Id, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return Result<DysonSkillExplorerEntry, string>.AsValue(match);

        // Fallback when search ranking omits the exact id.
        return Result<DysonSkillExplorerEntry, string>.AsValue(
            new DysonSkillExplorerEntry(
                Slug: parsed.Value.Id,
                Name: parsed.Value.SkillId,
                Description: "",
                Author: parsed.Value.Owner,
                Stars: 0,
                Verified: false,
                Tags: []));
    }

    public async Task<Result<DysonSkillExplorerPreviewOutcome, string>> PreviewSkillMarkdownAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        var zip = await DownloadSkillPackageZipAsync(slug, cancellationToken).ConfigureAwait(false);
        if (zip.IsError)
            return Result<DysonSkillExplorerPreviewOutcome, string>.AsError(zip.Error);

        var md = DysonSkillPackageInstall.ReadSkillMarkdownFromZip(zip.Value);
        if (md.IsError)
            return Result<DysonSkillExplorerPreviewOutcome, string>.AsError(md.Error);

        return Result<DysonSkillExplorerPreviewOutcome, string>.AsValue(
            new DysonSkillExplorerPreviewOutcome.Markdown(md.Value));
    }

    public async Task<Result<DysonSkillExplorerDownloadOutcome, string>> DownloadAsync(
        string slug,
        IDysonWorkspaceFileSystem fs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fs);
        if (!fs.IsInitialized)
        {
            return Result<DysonSkillExplorerDownloadOutcome, string>.AsError(
                "Workspace filesystem is not initialized.");
        }

        var parsed = ParseSlug(slug);
        if (parsed.IsError)
            return Result<DysonSkillExplorerDownloadOutcome, string>.AsError(parsed.Error);

        var safe = DysonSkillPackageInstall.SanitizeFolderSlug(parsed.Value.Id);
        if (safe.IsError)
            return Result<DysonSkillExplorerDownloadOutcome, string>.AsError(safe.Error);

        var zip = await DownloadSkillPackageZipAsync(parsed.Value.Id, cancellationToken).ConfigureAwait(false);
        if (zip.IsError)
            return Result<DysonSkillExplorerDownloadOutcome, string>.AsError(zip.Error);

        var extracted = DysonSkillPackageInstall.ExtractZipToSkillDir(zip.Value, safe.Value, fs);
        if (extracted.IsError)
            return Result<DysonSkillExplorerDownloadOutcome, string>.AsError(extracted.Error);

        return Result<DysonSkillExplorerDownloadOutcome, string>.AsValue(
            new DysonSkillExplorerDownloadOutcome.Installed(extracted.Value));
    }

    private async Task<Result<byte[], string>> DownloadSkillPackageZipAsync(
        string slug,
        CancellationToken cancellationToken)
    {
        var parsed = ParseSlug(slug);
        if (parsed.IsError)
            return Result<byte[], string>.AsError(parsed.Error);

        var repoZip = await DownloadGithubZipballAsync(parsed.Value.Source, cancellationToken)
            .ConfigureAwait(false);
        if (repoZip.IsError)
            return Result<byte[], string>.AsError(repoZip.Error);

        return DysonSkillPackageInstall.FilterZipToNamedSkillFolder(repoZip.Value, parsed.Value.SkillId);
    }

    private async Task<Result<byte[], string>> DownloadGithubZipballAsync(
        string source,
        CancellationToken cancellationToken)
    {
        // Absolute URL: works with skills.sh BaseAddress HttpClient.
        var url = "https://api.github.com/repos/" + source.Trim() + "/zipball";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            EnsureUserAgent(request);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return Result<byte[], string>.AsError(
                    $"GitHub zipball HTTP {(int)response.StatusCode}: {Truncate(body, 400)}");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
                return Result<byte[], string>.AsError("GitHub zipball returned an empty package.");

            return Result<byte[], string>.AsValue(bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<byte[], string>.AsError("GitHub zipball download was cancelled.");
        }
        catch (Exception ex)
        {
            return Result<byte[], string>.AsError("GitHub zipball download failed: " + ex.Message, ex);
        }
    }

    private async Task<Result<string, string>> GetStringAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
            EnsureUserAgent(request);

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<string, string>.AsError(
                    $"skills.sh HTTP {(int)response.StatusCode}: {Truncate(body, 400)}");
            }

            return Result<string, string>.AsValue(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<string, string>.AsError("skills.sh request was cancelled.");
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError("skills.sh request failed: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Slug is skills.sh id: <c>{source}/{skillId}</c> where source is <c>owner/repo</c>
    /// (e.g. <c>anthropics/skills/pdf</c>).
    /// </summary>
    internal static Result<ParsedSlug, string> ParseSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return Result<ParsedSlug, string>.AsError("slug is required.");

        var trimmed = slug.Trim().Replace('\\', '/').Trim('/');
        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            return Result<ParsedSlug, string>.AsError(
                $"Invalid skills.sh slug '{trimmed}' (expected owner/repo/skillId).");
        }

        foreach (var part in parts)
        {
            if (part is "." or ".." || part.Contains(':'))
                return Result<ParsedSlug, string>.AsError($"Invalid skills.sh slug '{trimmed}'.");
        }

        var skillId = parts[^1];
        var source = string.Join('/', parts[..^1]);
        var id = source + "/" + skillId;
        return Result<ParsedSlug, string>.AsValue(new ParsedSlug(id, source, skillId, parts[0]));
    }

    private static DysonSkillExplorerEntry MapEntry(SearchSkillDto dto)
    {
        var id = (dto.Id ?? "").Trim();
        if (string.IsNullOrEmpty(id)
            && !string.IsNullOrWhiteSpace(dto.Source)
            && !string.IsNullOrWhiteSpace(dto.SkillId))
        {
            id = dto.Source.Trim().TrimEnd('/') + "/" + dto.SkillId.Trim();
        }

        var skillId = dto.SkillId?.Trim() ?? "";
        var name = string.IsNullOrWhiteSpace(dto.Name) ? (skillId.Length > 0 ? skillId : id) : dto.Name.Trim();
        var author = dto.Source?.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return new DysonSkillExplorerEntry(
            Slug: id,
            Name: name,
            Description: "",
            Author: author,
            Stars: dto.Installs,
            Verified: false,
            Tags: []);
    }

    private static void EnsureUserAgent(HttpRequestMessage request)
    {
        if (request.Headers.UserAgent.Count == 0)
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DysonHarness", "1.0"));
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    internal readonly record struct ParsedSlug(string Id, string Source, string SkillId, string Owner);

    private sealed class SearchResponse
    {
        public List<SearchSkillDto>? Skills { get; set; }
    }

    private sealed class SearchSkillDto
    {
        public string? Id { get; set; }
        public string? SkillId { get; set; }
        public string? Name { get; set; }
        public int Installs { get; set; }
        public string? Source { get; set; }
    }
}
