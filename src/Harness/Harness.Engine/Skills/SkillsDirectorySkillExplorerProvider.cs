using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>
/// Skills Directory explorer: public registry search + site zip download/preview.
/// </summary>
public sealed class SkillsDirectorySkillExplorerProvider(HttpClient http) : IDysonSkillExplorerProvider
{
    public const string ProviderId = "skillsdirectory";
    public const string ProviderDisplayName = "Skills Directory";

    private const string RegistryPath = "api/registry";
    private const string DownloadPathFormat = "api/skills/{0}/download";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Regex SafeSlug = new(
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

        var clampedLimit = Math.Min(limit, 100);
        var qs = new List<string>
        {
            "limit=" + clampedLimit,
            "offset=" + offset,
        };
        if (!string.IsNullOrWhiteSpace(query))
            qs.Add("q=" + Uri.EscapeDataString(query.Trim()));

        var url = RegistryPath + "?" + string.Join('&', qs);
        var json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        if (json.IsError)
            return Result<DysonSkillExplorerSearchPage, string>.AsError(json.Error);

        try
        {
            var dto = JsonSerializer.Deserialize<RegistryListResponse>(json.Value, JsonOptions);
            if (dto?.Skills is null)
            {
                return Result<DysonSkillExplorerSearchPage, string>.AsError(
                    "Skills Directory registry returned an unexpected payload.");
            }

            var skills = dto.Skills.Select(MapListEntry).ToList();
            var pagination = dto.Pagination;
            var total = pagination?.Total ?? skills.Count;
            var pageLimit = pagination?.Limit ?? clampedLimit;
            var pageOffset = pagination?.Offset ?? offset;
            var hasMore = pagination?.HasMore ?? (pageOffset + skills.Count < total);

            return Result<DysonSkillExplorerSearchPage, string>.AsValue(
                new DysonSkillExplorerSearchPage(skills, total, pageLimit, pageOffset, hasMore));
        }
        catch (JsonException ex)
        {
            return Result<DysonSkillExplorerSearchPage, string>.AsError(
                "Skills Directory registry JSON was invalid: " + ex.Message, ex);
        }
    }

    public async Task<Result<DysonSkillExplorerEntry, string>> GetAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        var validated = ValidateSlug(slug);
        if (validated.IsError)
            return Result<DysonSkillExplorerEntry, string>.AsError(validated.Error);

        var url = RegistryPath + "/" + Uri.EscapeDataString(validated.Value);
        var json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        if (json.IsError)
            return Result<DysonSkillExplorerEntry, string>.AsError(json.Error);

        try
        {
            var dto = JsonSerializer.Deserialize<RegistryDetailResponse>(json.Value, JsonOptions);
            if (dto?.Skill is null)
            {
                return Result<DysonSkillExplorerEntry, string>.AsError(
                    $"Skill '{validated.Value}' not found in Skills Directory.");
            }

            return Result<DysonSkillExplorerEntry, string>.AsValue(MapDetailEntry(dto.Skill));
        }
        catch (JsonException ex)
        {
            return Result<DysonSkillExplorerEntry, string>.AsError(
                "Skills Directory skill JSON was invalid: " + ex.Message, ex);
        }
    }

    public async Task<Result<DysonSkillExplorerPreviewOutcome, string>> PreviewSkillMarkdownAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        var zip = await DownloadZipBytesAsync(slug, cancellationToken).ConfigureAwait(false);
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

        var validated = ValidateSlug(slug);
        if (validated.IsError)
            return Result<DysonSkillExplorerDownloadOutcome, string>.AsError(validated.Error);

        var zip = await DownloadZipBytesAsync(validated.Value, cancellationToken).ConfigureAwait(false);
        if (zip.IsError)
            return Result<DysonSkillExplorerDownloadOutcome, string>.AsError(zip.Error);

        var extracted = DysonSkillPackageInstall.ExtractZipToSkillDir(zip.Value, validated.Value, fs);
        if (extracted.IsError)
            return Result<DysonSkillExplorerDownloadOutcome, string>.AsError(extracted.Error);

        return Result<DysonSkillExplorerDownloadOutcome, string>.AsValue(
            new DysonSkillExplorerDownloadOutcome.Installed(extracted.Value));
    }

    private async Task<Result<byte[], string>> DownloadZipBytesAsync(
        string slug,
        CancellationToken cancellationToken)
    {
        var validated = ValidateSlug(slug);
        if (validated.IsError)
            return Result<byte[], string>.AsError(validated.Error);

        var url = string.Format(DownloadPathFormat, Uri.EscapeDataString(validated.Value));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            EnsureUserAgent(request);

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var snippet = Truncate(body, 400);
                return Result<byte[], string>.AsError(
                    $"Skills Directory download HTTP {(int)response.StatusCode}: {snippet}");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
                return Result<byte[], string>.AsError("Skills Directory download returned an empty package.");

            return Result<byte[], string>.AsValue(bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<byte[], string>.AsError("Skills Directory download was cancelled.");
        }
        catch (Exception ex)
        {
            return Result<byte[], string>.AsError("Skills Directory download failed: " + ex.Message, ex);
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
                var snippet = Truncate(body, 400);
                return Result<string, string>.AsError(
                    $"Skills Directory HTTP {(int)response.StatusCode}: {snippet}");
            }

            return Result<string, string>.AsValue(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<string, string>.AsError("Skills Directory request was cancelled.");
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError("Skills Directory request failed: " + ex.Message, ex);
        }
    }

    private static Result<string, string> ValidateSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return Result<string, string>.AsError("slug is required.");

        var trimmed = slug.Trim();
        if (trimmed.Length > 128 || !SafeSlug.IsMatch(trimmed))
            return Result<string, string>.AsError($"Invalid skill slug '{trimmed}'.");

        return Result<string, string>.AsValue(trimmed);
    }

    private static DysonSkillExplorerEntry MapListEntry(RegistrySkillDto dto)
    {
        var slug = dto.Slug?.Trim() ?? "";
        return new DysonSkillExplorerEntry(
            Slug: slug,
            Name: string.IsNullOrWhiteSpace(dto.Name) ? slug : dto.Name.Trim(),
            Description: dto.Description?.Trim() ?? "",
            Author: ReadAuthor(dto.Author),
            Stars: dto.Stars,
            Verified: dto.Verified,
            Tags: dto.Tags ?? []);
    }

    private static DysonSkillExplorerEntry MapDetailEntry(RegistrySkillDetailDto dto)
    {
        var slug = dto.Slug?.Trim() ?? "";
        var stars = dto.Github?.Stars ?? dto.Stars;
        return new DysonSkillExplorerEntry(
            Slug: slug,
            Name: string.IsNullOrWhiteSpace(dto.Name) ? slug : dto.Name.Trim(),
            Description: dto.Description?.Trim() ?? "",
            Author: ReadAuthor(dto.Author),
            Stars: stars,
            Verified: dto.Verified,
            Tags: dto.Tags ?? []);
    }

    private static string? ReadAuthor(JsonElement author)
    {
        return author.ValueKind switch
        {
            JsonValueKind.String => author.GetString(),
            JsonValueKind.Object when author.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String => name.GetString(),
            _ => null,
        };
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

    private sealed class RegistryListResponse
    {
        public List<RegistrySkillDto>? Skills { get; set; }
        public RegistryPaginationDto? Pagination { get; set; }
    }

    private sealed class RegistryDetailResponse
    {
        public RegistrySkillDetailDto? Skill { get; set; }
    }

    private sealed class RegistryPaginationDto
    {
        public int Total { get; set; }
        public int Limit { get; set; }
        public int Offset { get; set; }
        public bool HasMore { get; set; }
    }

    private sealed class RegistrySkillDto
    {
        public string? Name { get; set; }
        public string? Slug { get; set; }
        public string? Description { get; set; }
        public JsonElement Author { get; set; }
        public int Stars { get; set; }
        public bool Verified { get; set; }
        public string[]? Tags { get; set; }
    }

    private sealed class RegistrySkillDetailDto
    {
        public string? Name { get; set; }
        public string? Slug { get; set; }
        public string? Description { get; set; }
        public JsonElement Author { get; set; }
        public int Stars { get; set; }
        public bool Verified { get; set; }
        public string[]? Tags { get; set; }
        public RegistryGithubDto? Github { get; set; }
    }

    private sealed class RegistryGithubDto
    {
        public int Stars { get; set; }
    }
}
