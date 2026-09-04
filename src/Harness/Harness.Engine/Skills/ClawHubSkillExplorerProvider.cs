using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>
/// ClawHub explorer: public search/browse + SKILL.md file preview + zip/public-github download.
/// Composite slug format when owner is known: <c>ownerHandle/slug</c>.
/// </summary>
public sealed class ClawHubSkillExplorerProvider(HttpClient http) : IDysonSkillExplorerProvider
{
    public const string ProviderId = "clawhub";
    public const string ProviderDisplayName = "ClawHub";

    private const string SearchPath = "api/v1/search";
    private const string BrowsePath = "api/v1/skills";
    private const string DownloadPath = "api/v1/download";
    private const int MaxLimit = 100;

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

        // ponytail: search has no offset; browse is cursor-only — first page only for v1
        if (offset > 0)
        {
            return Result<DysonSkillExplorerSearchPage, string>.AsValue(
                new DysonSkillExplorerSearchPage([], 0, clampedLimit, offset, HasMore: false));
        }

        if (string.IsNullOrWhiteSpace(query))
            return await BrowseAsync(clampedLimit, cancellationToken).ConfigureAwait(false);

        var url = SearchPath + "?q=" + Uri.EscapeDataString(query.Trim()) + "&limit=" + clampedLimit;
        var json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        if (json.IsError)
            return Result<DysonSkillExplorerSearchPage, string>.AsError(json.Error);

        try
        {
            var dto = JsonSerializer.Deserialize<SearchResponse>(json.Value, JsonOptions);
            if (dto?.Results is null)
            {
                return Result<DysonSkillExplorerSearchPage, string>.AsError(
                    "ClawHub search returned an unexpected payload.");
            }

            var skills = dto.Results
                .Select(MapSearchEntry)
                .Where(e => e is not null)
                .Select(e => e!)
                .ToList();

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
                "ClawHub search JSON was invalid: " + ex.Message, ex);
        }
    }

    public async Task<Result<DysonSkillExplorerEntry, string>> GetAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        var parsed = ParseSlug(slug);
        if (parsed.IsError)
            return Result<DysonSkillExplorerEntry, string>.AsError(parsed.Error);

        var url = BrowsePath + "/" + Uri.EscapeDataString(parsed.Value.SkillSlug)
            + OwnerQuery(parsed.Value.OwnerHandle, leadingAmpersand: false);
        var json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        if (json.IsError)
            return Result<DysonSkillExplorerEntry, string>.AsError(json.Error);

        try
        {
            var dto = JsonSerializer.Deserialize<SkillDetailResponse>(json.Value, JsonOptions);
            if (dto?.Skill is null)
            {
                return Result<DysonSkillExplorerEntry, string>.AsError(
                    $"Skill '{parsed.Value.DisplaySlug}' not found in ClawHub.");
            }

            return Result<DysonSkillExplorerEntry, string>.AsValue(
                MapDetailEntry(dto, parsed.Value.DisplaySlug));
        }
        catch (JsonException ex)
        {
            return Result<DysonSkillExplorerEntry, string>.AsError(
                "ClawHub skill JSON was invalid: " + ex.Message, ex);
        }
    }

    public async Task<Result<DysonSkillExplorerPreviewOutcome, string>> PreviewSkillMarkdownAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        var parsed = ParseSlug(slug);
        if (parsed.IsError)
            return Result<DysonSkillExplorerPreviewOutcome, string>.AsError(parsed.Error);

        var url = BrowsePath + "/" + Uri.EscapeDataString(parsed.Value.SkillSlug)
            + "/file?path=" + Uri.EscapeDataString("SKILL.md")
            + OwnerQuery(parsed.Value.OwnerHandle, leadingAmpersand: true);

        var body = await GetStringAllowingAmbiguousAsync(url, cancellationToken).ConfigureAwait(false);
        if (body.IsError)
            return Result<DysonSkillExplorerPreviewOutcome, string>.AsError(body.Error);

        if (body.Value.Matches is not null)
        {
            return Result<DysonSkillExplorerPreviewOutcome, string>.AsValue(
                new DysonSkillExplorerPreviewOutcome.Ambiguous(body.Value.Matches));
        }

        return Result<DysonSkillExplorerPreviewOutcome, string>.AsValue(
            new DysonSkillExplorerPreviewOutcome.Markdown(body.Value.Text ?? ""));
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

        var folder = DysonSkillPackageInstall.SanitizeFolderSlug(parsed.Value.DisplaySlug);
        if (folder.IsError)
            return Result<DysonSkillExplorerDownloadOutcome, string>.AsError(folder.Error);

        var zip = await ResolvePackageZipAsync(parsed.Value, cancellationToken).ConfigureAwait(false);
        if (zip.IsError)
            return Result<DysonSkillExplorerDownloadOutcome, string>.AsError(zip.Error);

        if (zip.Value.Matches is not null)
        {
            return Result<DysonSkillExplorerDownloadOutcome, string>.AsValue(
                new DysonSkillExplorerDownloadOutcome.Ambiguous(zip.Value.Matches));
        }

        var extracted = await DysonSkillPackageInstall
            .ExtractZipToSkillDirAsync(zip.Value.ZipBytes!, folder.Value, fs, cancellationToken)
            .ConfigureAwait(false);
        if (extracted.IsError)
            return Result<DysonSkillExplorerDownloadOutcome, string>.AsError(extracted.Error);

        return Result<DysonSkillExplorerDownloadOutcome, string>.AsValue(
            new DysonSkillExplorerDownloadOutcome.Installed(extracted.Value));
    }

    private async Task<Result<DysonSkillExplorerSearchPage, string>> BrowseAsync(
        int clampedLimit,
        CancellationToken cancellationToken)
    {
        var url = BrowsePath + "?limit=" + clampedLimit + "&sort=downloads";
        var json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        if (json.IsError)
            return Result<DysonSkillExplorerSearchPage, string>.AsError(json.Error);

        try
        {
            var dto = JsonSerializer.Deserialize<BrowseResponse>(json.Value, JsonOptions);
            if (dto?.Items is null)
            {
                return Result<DysonSkillExplorerSearchPage, string>.AsError(
                    "ClawHub browse returned an unexpected payload.");
            }

            var skills = dto.Items
                .Select(MapBrowseEntry)
                .Where(e => e is not null)
                .Select(e => e!)
                .ToList();

            // ponytail: cursor-only pagination — do not advertise HasMore (UI would offset-page)
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
                "ClawHub browse JSON was invalid: " + ex.Message, ex);
        }
    }

    private async Task<Result<ZipOrAmbiguous, string>> ResolvePackageZipAsync(
        ParsedSlug parsed,
        CancellationToken cancellationToken)
    {
        var url = DownloadPath + "?slug=" + Uri.EscapeDataString(parsed.SkillSlug)
            + OwnerQuery(parsed.OwnerHandle, leadingAmpersand: true);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            EnsureUserAgent(request);

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var bodyText = Encoding.UTF8.GetString(bytes);
            if (!response.IsSuccessStatusCode)
            {
                if (TryParseAmbiguousMatches(response.StatusCode, bodyText, out var matches))
                    return Result<ZipOrAmbiguous, string>.AsValue(ZipOrAmbiguous.FromMatches(matches));

                return Result<ZipOrAmbiguous, string>.AsError(FormatHttpError(response, bodyText));
            }

            if (bytes.Length == 0)
                return Result<ZipOrAmbiguous, string>.AsError("ClawHub download returned an empty package.");

            if (LooksLikeZip(bytes))
                return Result<ZipOrAmbiguous, string>.AsValue(ZipOrAmbiguous.FromZip(bytes));

            if (!LooksLikeJson(bytes, response.Content.Headers.ContentType?.MediaType))
            {
                return Result<ZipOrAmbiguous, string>.AsError(
                    "ClawHub download returned an unrecognized package payload.");
            }

            var handoff = await ResolveGithubHandoffZipAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (handoff.IsError)
                return Result<ZipOrAmbiguous, string>.AsError(handoff.Error);

            return Result<ZipOrAmbiguous, string>.AsValue(ZipOrAmbiguous.FromZip(handoff.Value));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<ZipOrAmbiguous, string>.AsError("ClawHub download was cancelled.");
        }
        catch (Exception ex)
        {
            return Result<ZipOrAmbiguous, string>.AsError("ClawHub download failed: " + ex.Message, ex);
        }
    }

    private async Task<Result<byte[], string>> ResolveGithubHandoffZipAsync(
        byte[] jsonBytes,
        CancellationToken cancellationToken)
    {
        GithubHandoffDto? handoff;
        try
        {
            handoff = JsonSerializer.Deserialize<GithubHandoffDto>(jsonBytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Result<byte[], string>.AsError(
                "ClawHub download JSON was invalid: " + ex.Message, ex);
        }

        if (handoff is null
            || !string.Equals(handoff.SourceRef, "public-github", StringComparison.OrdinalIgnoreCase))
        {
            return Result<byte[], string>.AsError(
                "ClawHub download JSON was not a public-github handoff.");
        }

        if (string.IsNullOrWhiteSpace(handoff.ArchiveUrl)
            || !Uri.TryCreate(handoff.ArchiveUrl.Trim(), UriKind.Absolute, out var archiveUri)
            || (archiveUri.Scheme != Uri.UriSchemeHttps && archiveUri.Scheme != Uri.UriSchemeHttp))
        {
            return Result<byte[], string>.AsError(
                "ClawHub public-github handoff is missing a valid archiveUrl.");
        }

        var zip = await DownloadAbsoluteBytesAsync(archiveUri, cancellationToken).ConfigureAwait(false);
        if (zip.IsError)
            return Result<byte[], string>.AsError(zip.Error);

        var path = handoff.Path?.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(path))
            return Result<byte[], string>.AsValue(zip.Value);

        // ponytail: match skill folder by last path segment; upgrade to full-path filter if collisions appear
        var folderName = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        return DysonSkillPackageInstall.FilterZipToNamedSkillFolder(zip.Value, folderName);
    }

    private async Task<Result<byte[], string>> DownloadAbsoluteBytesAsync(
        Uri absoluteUri,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, absoluteUri);
            EnsureUserAgent(request);

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<byte[], string>.AsError(
                    FormatHttpError(response, Encoding.UTF8.GetString(bytes), prefix: "GitHub archive"));
            }

            if (bytes.Length == 0)
                return Result<byte[], string>.AsError("GitHub archive returned an empty package.");

            if (!LooksLikeZip(bytes))
                return Result<byte[], string>.AsError("GitHub archive was not a valid zip.");

            return Result<byte[], string>.AsValue(bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<byte[], string>.AsError("GitHub archive download was cancelled.");
        }
        catch (Exception ex)
        {
            return Result<byte[], string>.AsError("GitHub archive download failed: " + ex.Message, ex);
        }
    }

    private async Task<Result<string, string>> GetStringAsync(
        string relativeUrl,
        CancellationToken cancellationToken)
    {
        var result = await GetStringAllowingAmbiguousAsync(relativeUrl, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsError)
            return Result<string, string>.AsError(result.Error);

        if (result.Value.Matches is not null)
        {
            return Result<string, string>.AsError(
                "ClawHub HTTP 409: AMBIGUOUS_SKILL_SLUG ("
                + result.Value.Matches.Count
                + " matches).");
        }

        return Result<string, string>.AsValue(result.Value.Text ?? "");
    }

    private async Task<Result<TextOrAmbiguous, string>> GetStringAllowingAmbiguousAsync(
        string relativeUrl,
        CancellationToken cancellationToken)
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
                if (TryParseAmbiguousMatches(response.StatusCode, body, out var matches))
                    return Result<TextOrAmbiguous, string>.AsValue(TextOrAmbiguous.FromMatches(matches));

                return Result<TextOrAmbiguous, string>.AsError(FormatHttpError(response, body));
            }

            return Result<TextOrAmbiguous, string>.AsValue(TextOrAmbiguous.FromText(body));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<TextOrAmbiguous, string>.AsError("ClawHub request was cancelled.");
        }
        catch (Exception ex)
        {
            return Result<TextOrAmbiguous, string>.AsError("ClawHub request failed: " + ex.Message, ex);
        }
    }

    private static DysonSkillExplorerEntry? MapSearchEntry(SearchResultDto dto)
    {
        var skillSlug = dto.Slug?.Trim();
        if (string.IsNullOrWhiteSpace(skillSlug))
            return null;

        var owner = dto.OwnerHandle?.Trim()
            ?? dto.Owner?.Handle?.Trim();
        if (string.IsNullOrWhiteSpace(owner)
            && dto.Install?.Reference?.Trim() is { Length: > 0 } reference)
        {
            var slash = reference.IndexOf('/');
            if (slash > 0)
                owner = reference[..slash];
        }

        var displaySlug = string.IsNullOrWhiteSpace(owner) ? skillSlug : owner + "/" + skillSlug;
        var name = string.IsNullOrWhiteSpace(dto.DisplayName) ? skillSlug : dto.DisplayName.Trim();
        var description = dto.Summary?.Trim() ?? "";
        var author = dto.Owner?.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(author))
            author = owner;

        var stars = dto.Native?.Skill?.Stats?.Stars
            ?? dto.Downloads
            ?? 0;

        var topics = dto.Native?.Skill?.Topics ?? [];

        return new DysonSkillExplorerEntry(
            Slug: displaySlug,
            Name: name,
            Description: description,
            Author: author,
            Stars: stars,
            Verified: dto.Official,
            Tags: topics);
    }

    private static DysonSkillExplorerEntry? MapBrowseEntry(BrowseItemDto dto)
    {
        var skillSlug = dto.Slug?.Trim();
        if (string.IsNullOrWhiteSpace(skillSlug))
            return null;

        var owner = dto.OwnerHandle?.Trim()
            ?? dto.Owner?.Handle?.Trim();
        var displaySlug = string.IsNullOrWhiteSpace(owner) ? skillSlug : owner + "/" + skillSlug;

        var name = string.IsNullOrWhiteSpace(dto.DisplayName) ? skillSlug : dto.DisplayName.Trim();
        var description = !string.IsNullOrWhiteSpace(dto.Summary)
            ? dto.Summary.Trim()
            : dto.Description?.Trim() ?? "";
        var author = dto.Owner?.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(author))
            author = owner;

        return new DysonSkillExplorerEntry(
            Slug: displaySlug,
            Name: name,
            Description: description,
            Author: author,
            Stars: dto.Stats?.Stars ?? dto.Stats?.Downloads ?? 0,
            Verified: false,
            Tags: dto.Topics ?? []);
    }

    private static bool TryParseAmbiguousMatches(
        HttpStatusCode status,
        string body,
        out IReadOnlyList<DysonSkillExplorerMatch> matches)
    {
        matches = [];
        if (status != HttpStatusCode.Conflict || string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            var dto = JsonSerializer.Deserialize<AmbiguousErrorDto>(body, JsonOptions);
            if (dto is null
                || !IsAmbiguousSkillSlug(dto)
                || dto.Matches is null
                || dto.Matches.Count == 0)
            {
                return false;
            }

            var mapped = new List<DysonSkillExplorerMatch>(dto.Matches.Count);
            foreach (var item in dto.Matches)
            {
                var skillSlug = item.Slug?.Trim();
                if (string.IsNullOrWhiteSpace(skillSlug))
                    continue;

                var owner = item.OwnerHandle?.Trim();
                var retrySlug = string.IsNullOrWhiteSpace(owner)
                    ? skillSlug
                    : owner + "/" + skillSlug;
                var reference = item.Ref?.Trim();
                var label = !string.IsNullOrWhiteSpace(reference)
                    ? reference
                    : retrySlug;

                mapped.Add(new DysonSkillExplorerMatch(
                    Slug: retrySlug,
                    Label: label,
                    OwnerHandle: owner,
                    Ref: reference));
            }

            if (mapped.Count == 0)
                return false;

            matches = mapped;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DysonSkillExplorerEntry MapDetailEntry(SkillDetailResponse dto, string displaySlug)
    {
        var skill = dto.Skill!;
        var skillSlug = skill.Slug?.Trim() ?? displaySlug;
        var owner = dto.Owner?.Handle?.Trim();
        var composed = string.IsNullOrWhiteSpace(owner) ? skillSlug : owner + "/" + skillSlug;
        // Prefer caller's composite when provided so UI/install keep the same id.
        var slug = displaySlug.Contains('/') ? displaySlug : composed;

        var name = string.IsNullOrWhiteSpace(skill.DisplayName) ? skillSlug : skill.DisplayName.Trim();
        var description = !string.IsNullOrWhiteSpace(skill.Summary)
            ? skill.Summary.Trim()
            : skill.Description?.Trim() ?? "";
        var author = dto.Owner?.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(author))
            author = owner;

        return new DysonSkillExplorerEntry(
            Slug: slug,
            Name: name,
            Description: description,
            Author: author,
            Stars: skill.Stats?.Stars ?? skill.Stats?.Downloads ?? 0,
            Verified: false,
            Tags: skill.Topics ?? []);
    }

    private static Result<ParsedSlug, string> ParseSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return Result<ParsedSlug, string>.AsError("slug is required.");

        var trimmed = slug.Trim().TrimStart('@');
        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            if (parts[0].Length > 128 || !SegmentOk.IsMatch(parts[0]))
                return Result<ParsedSlug, string>.AsError($"Invalid skill slug '{trimmed}'.");

            return Result<ParsedSlug, string>.AsValue(new ParsedSlug(null, parts[0], parts[0]));
        }

        if (parts.Length == 2)
        {
            var owner = parts[0];
            var skill = parts[1];
            if (owner.Length > 128 || skill.Length > 128
                || !SegmentOk.IsMatch(owner)
                || !SegmentOk.IsMatch(skill))
            {
                return Result<ParsedSlug, string>.AsError($"Invalid skill slug '{trimmed}'.");
            }

            return Result<ParsedSlug, string>.AsValue(
                new ParsedSlug(owner, skill, owner + "/" + skill));
        }

        return Result<ParsedSlug, string>.AsError(
            $"Invalid ClawHub slug '{trimmed}' (expected slug or ownerHandle/slug).");
    }

    private static string OwnerQuery(string? ownerHandle, bool leadingAmpersand)
    {
        if (string.IsNullOrWhiteSpace(ownerHandle))
            return "";

        var prefix = leadingAmpersand ? "&" : "?";
        return prefix + "ownerHandle=" + Uri.EscapeDataString(ownerHandle.Trim());
    }

    private static string FormatHttpError(
        HttpResponseMessage response,
        string body,
        string prefix = "ClawHub")
    {
        var code = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds
                ?? (response.Headers.RetryAfter?.Date is { } date
                    ? Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds)
                    : null);

            if (retryAfter is null
                && response.Headers.TryGetValues("Retry-After", out var values))
            {
                var raw = values.FirstOrDefault();
                if (double.TryParse(raw, out var seconds))
                    retryAfter = seconds;
            }

            if (retryAfter is not null)
            {
                return prefix
                    + " HTTP 429 (rate limited). Retry-After: "
                    + ((int)Math.Ceiling(retryAfter.Value))
                    + "s.";
            }

            return prefix + " HTTP 429 (rate limited).";
        }

        return prefix + " HTTP " + code + ": " + Truncate(body, 400);
    }

    private static bool LooksLikeZip(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K';

    private static bool LooksLikeJson(byte[] bytes, string? mediaType)
    {
        if (mediaType is not null
            && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                continue;
            return b is (byte)'{' or (byte)'[';
        }

        return false;
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

    private readonly record struct ParsedSlug(string? OwnerHandle, string SkillSlug, string DisplaySlug);

    private readonly record struct ZipOrAmbiguous(
        byte[]? ZipBytes,
        IReadOnlyList<DysonSkillExplorerMatch>? Matches)
    {
        public static ZipOrAmbiguous FromZip(byte[] bytes) => new(bytes, null);

        public static ZipOrAmbiguous FromMatches(IReadOnlyList<DysonSkillExplorerMatch> matches) =>
            new(null, matches);
    }

    private readonly record struct TextOrAmbiguous(
        string? Text,
        IReadOnlyList<DysonSkillExplorerMatch>? Matches)
    {
        public static TextOrAmbiguous FromText(string text) => new(text, null);

        public static TextOrAmbiguous FromMatches(IReadOnlyList<DysonSkillExplorerMatch> matches) =>
            new(null, matches);
    }

    private sealed class SearchResponse
    {
        public List<SearchResultDto>? Results { get; set; }
    }

    private sealed class SearchResultDto
    {
        public string? Slug { get; set; }
        public string? DisplayName { get; set; }
        public string? Summary { get; set; }
        public string? OwnerHandle { get; set; }
        public bool Official { get; set; }
        public int? Downloads { get; set; }
        public OwnerDto? Owner { get; set; }
        public InstallDto? Install { get; set; }
        public NativeDto? Native { get; set; }
    }

    private sealed class BrowseResponse
    {
        public List<BrowseItemDto>? Items { get; set; }
        public string? NextCursor { get; set; }
    }

    private sealed class BrowseItemDto
    {
        public string? Slug { get; set; }
        public string? DisplayName { get; set; }
        public string? Summary { get; set; }
        public string? Description { get; set; }
        public string? OwnerHandle { get; set; }
        public string[]? Topics { get; set; }
        public OwnerDto? Owner { get; set; }
        public StatsDto? Stats { get; set; }
    }

    private static bool IsAmbiguousSkillSlug(AmbiguousErrorDto dto) =>
        string.Equals(dto.Code, "AMBIGUOUS_SKILL_SLUG", StringComparison.OrdinalIgnoreCase)
        || string.Equals(dto.Error, "AMBIGUOUS_SKILL_SLUG", StringComparison.OrdinalIgnoreCase);

    private sealed class AmbiguousErrorDto
    {
        public string? Code { get; set; } // live API
        public string? Error { get; set; } // legacy
        public List<AmbiguousMatchDto>? Matches { get; set; }
    }

    private sealed class AmbiguousMatchDto
    {
        public string? OwnerHandle { get; set; }
        public string? Slug { get; set; }
        public string? Ref { get; set; }
    }

    private sealed class SkillDetailResponse
    {
        public SkillDto? Skill { get; set; }
        public OwnerDto? Owner { get; set; }
    }

    private sealed class SkillDto
    {
        public string? Slug { get; set; }
        public string? DisplayName { get; set; }
        public string? Summary { get; set; }
        public string? Description { get; set; }
        public string[]? Topics { get; set; }
        public StatsDto? Stats { get; set; }
    }

    private sealed class StatsDto
    {
        public int Stars { get; set; }
        public int Downloads { get; set; }
    }

    private sealed class OwnerDto
    {
        public string? Handle { get; set; }
        public string? DisplayName { get; set; }
    }

    private sealed class InstallDto
    {
        public string? Reference { get; set; }
    }

    private sealed class NativeDto
    {
        public NativeSkillDto? Skill { get; set; }
    }

    private sealed class NativeSkillDto
    {
        public StatsDto? Stats { get; set; }
        public string[]? Topics { get; set; }
    }

    private sealed class GithubHandoffDto
    {
        public string? SourceRef { get; set; }
        public string? Repo { get; set; }
        public string? Commit { get; set; }
        public string? Path { get; set; }
        public string? ContentHash { get; set; }
        public string? ArchiveUrl { get; set; }
    }
}
