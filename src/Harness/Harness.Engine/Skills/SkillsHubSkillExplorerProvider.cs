using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>
/// SkillsHub explorer: public search + markdown preview/install (no zip).
/// Composite slug format: <c>owner/repo/skill</c>.
/// </summary>
public sealed class SkillsHubSkillExplorerProvider(HttpClient http) : IDysonSkillExplorerProvider
{
    public const string ProviderId = "skillshub";
    public const string ProviderDisplayName = "SkillsHub";

    private const string SearchPath = "api/v1/skills/search";
    private const int MaxLimit = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Regex SegmentOk = new(
        @"^[a-zA-Z0-9]([a-zA-Z0-9._-]*[a-zA-Z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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

        var clampedLimit = Math.Min(limit, MaxLimit);
        var page = (offset / clampedLimit) + 1;

        var qs = new List<string>
        {
            "page=" + page,
            "limit=" + clampedLimit,
            "sort=stars",
        };
        if (!string.IsNullOrWhiteSpace(query))
            qs.Add("q=" + Uri.EscapeDataString(query.Trim()));

        var url = SearchPath + "?" + string.Join('&', qs);
        var json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        if (json.IsError)
            return Result<DysonSkillExplorerSearchPage, string>.AsError(json.Error);

        try
        {
            var dto = JsonSerializer.Deserialize<SearchResponse>(json.Value, JsonOptions);
            if (dto?.Data is null)
            {
                return Result<DysonSkillExplorerSearchPage, string>.AsError(
                    "SkillsHub search returned an unexpected payload.");
            }

            var skills = dto.Data
                .Select(MapEntry)
                .Where(e => e is not null)
                .Select(e => e!)
                .ToList();

            var total = dto.Total;
            var pageLimit = dto.Limit > 0 ? dto.Limit : clampedLimit;
            var pageNumber = dto.Page > 0 ? dto.Page : page;
            var pageOffset = (pageNumber - 1) * pageLimit;
            var hasMore = dto.HasMore;

            return Result<DysonSkillExplorerSearchPage, string>.AsValue(
                new DysonSkillExplorerSearchPage(skills, total, pageLimit, pageOffset, hasMore));
        }
        catch (JsonException ex)
        {
            return Result<DysonSkillExplorerSearchPage, string>.AsError(
                "SkillsHub search JSON was invalid: " + ex.Message, ex);
        }
    }

    public async Task<Result<DysonSkillExplorerEntry, string>> GetAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        var parts = ParseCompositeSlug(slug);
        if (parts.IsError)
            return Result<DysonSkillExplorerEntry, string>.AsError(parts.Error);

        var (owner, repo, skill) = parts.Value;
        var qs = new List<string>
        {
            "owner=" + Uri.EscapeDataString(owner),
            "repo=" + Uri.EscapeDataString(repo),
            "q=" + Uri.EscapeDataString(skill),
            "limit=" + MaxLimit,
            "sort=stars",
        };
        var url = SearchPath + "?" + string.Join('&', qs);
        var json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        if (json.IsError)
            return Result<DysonSkillExplorerEntry, string>.AsError(json.Error);

        try
        {
            var dto = JsonSerializer.Deserialize<SearchResponse>(json.Value, JsonOptions);
            if (dto?.Data is null)
            {
                return Result<DysonSkillExplorerEntry, string>.AsError(
                    "SkillsHub get returned an unexpected payload.");
            }

            var wanted = owner + "/" + repo + "/" + skill;
            foreach (var row in dto.Data)
            {
                var mapped = MapEntry(row);
                if (mapped is not null
                    && string.Equals(mapped.Slug, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return Result<DysonSkillExplorerEntry, string>.AsValue(mapped);
                }
            }

            return Result<DysonSkillExplorerEntry, string>.AsError(
                $"Skill '{wanted}' not found in SkillsHub.");
        }
        catch (JsonException ex)
        {
            return Result<DysonSkillExplorerEntry, string>.AsError(
                "SkillsHub get JSON was invalid: " + ex.Message, ex);
        }
    }

    public async Task<Result<string, string>> PreviewSkillMarkdownAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        var parts = ParseCompositeSlug(slug);
        if (parts.IsError)
            return Result<string, string>.AsError(parts.Error);

        var (owner, repo, skill) = parts.Value;
        var url = string.Join(
            '/',
            Uri.EscapeDataString(owner),
            Uri.EscapeDataString(repo),
            Uri.EscapeDataString(skill)) + "?format=md";

        return await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<string, string>> DownloadAsync(
        string slug,
        IDysonWorkspaceFileSystem fs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fs);
        if (!fs.IsInitialized)
            return Result<string, string>.AsError("Workspace filesystem is not initialized.");

        var md = await PreviewSkillMarkdownAsync(slug, cancellationToken).ConfigureAwait(false);
        if (md.IsError)
            return Result<string, string>.AsError(md.Error);

        if (string.IsNullOrWhiteSpace(md.Value))
            return Result<string, string>.AsError("SkillsHub returned empty skill markdown.");

        var folder = DysonSkillPackageInstall.SanitizeFolderSlug(slug.Trim());
        if (folder.IsError)
            return Result<string, string>.AsError(folder.Error);

        return DysonSkillPackageInstall.WriteSkillMarkdown(md.Value, folder.Value, fs);
    }

    private static DysonSkillExplorerEntry? MapEntry(SkillDto dto)
    {
        var owner = dto.Repo?.GithubOwner?.Trim()
            ?? dto.Owner?.Username?.Trim();
        var repo = dto.Repo?.GithubRepoName?.Trim();
        var skill = dto.Slug?.Trim();
        if (string.IsNullOrWhiteSpace(owner)
            || string.IsNullOrWhiteSpace(repo)
            || string.IsNullOrWhiteSpace(skill))
        {
            return null;
        }

        var composite = owner + "/" + repo + "/" + skill;
        var name = string.IsNullOrWhiteSpace(dto.Name) ? skill : dto.Name.Trim();
        var author = dto.Owner?.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(author))
            author = dto.Owner?.Username?.Trim() ?? owner;

        return new DysonSkillExplorerEntry(
            Slug: composite,
            Name: name,
            Description: dto.Description?.Trim() ?? "",
            Author: author,
            Stars: dto.Repo?.StarCount ?? 0,
            Verified: false,
            Tags: dto.Tags ?? []);
    }

    private static Result<(string Owner, string Repo, string Skill), string> ParseCompositeSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return Result<(string, string, string), string>.AsError("slug is required.");

        var trimmed = slug.Trim();
        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return Result<(string, string, string), string>.AsError(
                $"Invalid SkillsHub slug '{trimmed}' (expected owner/repo/skill).");
        }

        var owner = parts[0];
        var repo = parts[1];
        var skill = parts[2];
        if (owner.Length > 128 || repo.Length > 128 || skill.Length > 128
            || !SegmentOk.IsMatch(owner)
            || !SegmentOk.IsMatch(repo)
            || !SegmentOk.IsMatch(skill))
        {
            return Result<(string, string, string), string>.AsError(
                $"Invalid SkillsHub slug '{trimmed}'.");
        }

        return Result<(string, string, string), string>.AsValue((owner, repo, skill));
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
                var snippet = Truncate(body, 400);
                return Result<string, string>.AsError(
                    $"SkillsHub HTTP {(int)response.StatusCode}: {snippet}");
            }

            return Result<string, string>.AsValue(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<string, string>.AsError("SkillsHub request was cancelled.");
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError("SkillsHub request failed: " + ex.Message, ex);
        }
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

    private sealed class SearchResponse
    {
        public List<SkillDto>? Data { get; set; }
        public int Total { get; set; }
        public int Page { get; set; }
        public int Limit { get; set; }
        public bool HasMore { get; set; }
    }

    private sealed class SkillDto
    {
        public string? Id { get; set; }
        public string? Slug { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string[]? Tags { get; set; }
        public RepoDto? Repo { get; set; }
        public OwnerDto? Owner { get; set; }
    }

    private sealed class RepoDto
    {
        public int StarCount { get; set; }
        public string? GithubOwner { get; set; }
        public string? GithubRepoName { get; set; }
    }

    private sealed class OwnerDto
    {
        public string? Username { get; set; }
        public string? DisplayName { get; set; }
    }
}
