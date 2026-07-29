using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DysonHarness;

/// <summary>Include mode for an openrules Rules/Skills entry.</summary>
public static class DysonOpenRulesModes
{
    public const string AutoInclude = "AutoInclude";
    public const string AgentOptional = "AgentOptional";

    public static bool IsKnown(string? mode) =>
        string.Equals(mode, AutoInclude, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, AgentOptional, StringComparison.OrdinalIgnoreCase);

    public static bool IsAutoInclude(string? mode) =>
        string.Equals(mode, AutoInclude, StringComparison.OrdinalIgnoreCase);

    public static bool IsAgentOptional(string? mode) =>
        string.Equals(mode, AgentOptional, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Runtime provider ids used by openrules <c>Providers</c> filtering.</summary>
public static class DysonOpenRulesProviders
{
    /// <summary>Dyson harness runtime id.</summary>
    public const string Dyson = "dyson";
}

/// <summary>Raw entry in <c>openrules.json</c> (Rules or Skills).</summary>
public sealed class DysonOpenRulesEntryDto
{
    public string? Path { get; set; }
    public string? Mode { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Optional provider filter. Null/empty → all agents; otherwise load only when the
    /// runtime provider id is listed (case-insensitive).
    /// </summary>
    public string[]? Providers { get; set; }
}

/// <summary>Raw <c>openrules.json</c> document (PascalCase property names).</summary>
public sealed class DysonOpenRulesDocumentDto
{
    public string? Root { get; set; }
    public List<DysonOpenRulesEntryDto>? Rules { get; set; }
    public List<DysonOpenRulesEntryDto>? Skills { get; set; }
}

/// <summary>Resolved openrules entry with existence check (no file body).</summary>
public sealed class DysonOpenRulesResolvedEntry
{
    public required string Path { get; init; }
    public required string Mode { get; init; }
    public string? Description { get; init; }
    public required bool Exists { get; init; }
    public required bool IsSkill { get; init; }

    /// <summary>True when <see cref="Path"/> is an absolute http(s) URL.</summary>
    public required bool IsUrl { get; init; }

    /// <summary>
    /// Optional provider filter. Null/empty → all agents; otherwise load only when the
    /// runtime provider id is listed (case-insensitive).
    /// </summary>
    public IReadOnlyList<string>? Providers { get; init; }
}

/// <summary>Loaded openrules config (manifest or implicit default).</summary>
public sealed class DysonOpenRulesConfig
{
    /// <summary>True when <c>openrules.json</c> was present and parsed.</summary>
    public required bool ManifestPresent { get; init; }

    /// <summary>Work-relative Root path (default <c>AGENTS.md</c>).</summary>
    public required string RootPath { get; init; }

    public required bool RootExists { get; init; }

    public required IReadOnlyList<DysonOpenRulesResolvedEntry> Rules { get; init; }

    public required IReadOnlyList<DysonOpenRulesResolvedEntry> Skills { get; init; }
}

/// <summary>
/// Loads work-root <c>openrules.json</c>, builds AutoInclude system-prompt block, and
/// exposes AgentOptional entries for <see cref="DysonSkillLoader"/> / <c>GetOpenRulesConfig</c>.
/// </summary>
public static class DysonOpenRules
{
    public const string ManifestFileName = "openrules.json";
    public const string DefaultRootPath = "AGENTS.md";

    /// <summary>Canonical EntitySystems openrules skill URL (InitializeOpenRules default).</summary>
    public const string DefaultOpenRulesSkillUrl =
        "https://github.com/EntitySystems/openrules/blob/main/SKILL.md";

    /// <summary>Soft cap per Root/AutoInclude file body (chars).</summary>
    public const int MaxCharsPerFile = 50_000;

    /// <summary>Soft cap for the entire open-rules system-prompt block (chars).</summary>
    public const int MaxTotalChars = 100_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>True when <paramref name="path"/> is an absolute http(s) URL.</summary>
    public static bool IsPathUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (!Uri.TryCreate(path.Trim(), UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme is "http" or "https";
    }

    /// <summary>
    /// Empty/omitted <paramref name="providers"/> → all agents; otherwise case-insensitive
    /// contains match on <paramref name="providerId"/>.
    /// </summary>
    public static bool AppliesToProvider(IReadOnlyList<string>? providers, string? providerId)
    {
        if (providers is null || providers.Count == 0)
            return true;

        var id = string.IsNullOrWhiteSpace(providerId)
            ? DysonOpenRulesProviders.Dyson
            : providerId.Trim();

        for (var i = 0; i < providers.Count; i++)
        {
            var p = providers[i];
            if (!string.IsNullOrWhiteSpace(p)
                && string.Equals(p.Trim(), id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Convenience overload for resolved entries.</summary>
    public static bool AppliesToProvider(DysonOpenRulesResolvedEntry entry, string? providerId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return AppliesToProvider(entry.Providers, providerId);
    }

    /// <summary>
    /// Loads openrules from the work root. Missing manifest → implicit
    /// <c>{ Root: AGENTS.md, Rules: [], Skills: [] }</c> when <c>AGENTS.md</c> exists;
    /// otherwise returns null (no open-rules block).
    /// </summary>
    public static Result<DysonOpenRulesConfig?, string> TryLoad(IDysonWorkspaceFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(fs);
        if (!fs.IsInitialized)
            return Result<DysonOpenRulesConfig?, string>.AsError("Workspace filesystem is not initialized.");

        var manifestExists = fs.FileExists(ManifestFileName);
        if (manifestExists.IsError)
            return Result<DysonOpenRulesConfig?, string>.AsError(manifestExists.Error);

        if (!manifestExists.Value)
        {
            var agentsExists = fs.FileExists(DefaultRootPath);
            if (agentsExists.IsError)
                return Result<DysonOpenRulesConfig?, string>.AsError(agentsExists.Error);
            if (!agentsExists.Value)
                return Result<DysonOpenRulesConfig?, string>.AsValue(null);

            return Result<DysonOpenRulesConfig?, string>.AsValue(new DysonOpenRulesConfig
            {
                ManifestPresent = false,
                RootPath = DefaultRootPath,
                RootExists = true,
                Rules = [],
                Skills = [],
            });
        }

        var text = fs.ReadAllText(ManifestFileName);
        if (text.IsError)
            return Result<DysonOpenRulesConfig?, string>.AsError(text.Error);

        DysonOpenRulesDocumentDto? doc;
        try
        {
            doc = JsonSerializer.Deserialize<DysonOpenRulesDocumentDto>(text.Value, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Result<DysonOpenRulesConfig?, string>.AsError(
                $"Invalid {ManifestFileName}: {ex.Message}");
        }

        doc ??= new DysonOpenRulesDocumentDto();
        var rootPath = string.IsNullOrWhiteSpace(doc.Root)
            ? DefaultRootPath
            : NormalizePath(doc.Root);

        var rootExistsResult = fs.FileExists(rootPath);
        var rootExists = !rootExistsResult.IsError && rootExistsResult.Value;

        var rules = ResolveEntries(fs, doc.Rules, isSkill: false);
        if (rules.IsError)
            return Result<DysonOpenRulesConfig?, string>.AsError(rules.Error);

        var skills = ResolveEntries(fs, doc.Skills, isSkill: true);
        if (skills.IsError)
            return Result<DysonOpenRulesConfig?, string>.AsError(skills.Error);

        return Result<DysonOpenRulesConfig?, string>.AsValue(new DysonOpenRulesConfig
        {
            ManifestPresent = true,
            RootPath = rootPath,
            RootExists = rootExists,
            Rules = rules.Value,
            Skills = skills.Value,
        });
    }

    /// <summary>
    /// Default <c>openrules.json</c> document written by <c>InitializeOpenRules</c> when missing.
    /// Does not include <c>Providers</c> on the seeded skill (universal until authors filter).
    /// </summary>
    public static DysonOpenRulesDocumentDto CreateDefaultDocument() =>
        new()
        {
            Root = DefaultRootPath,
            Rules = [],
            Skills =
            [
                new DysonOpenRulesEntryDto
                {
                    Path = DefaultOpenRulesSkillUrl,
                    Mode = DysonOpenRulesModes.AgentOptional,
                    Description =
                        "OpenRules skill — how agents should load and interpret openrules.json",
                },
            ],
        };

    /// <summary>
    /// If <c>openrules.json</c> exists, returns its contents (<paramref name="created"/> false).
    /// If missing, writes the default document and returns it (<paramref name="created"/> true).
    /// </summary>
    public static Result<(string Json, bool Created), string> InitializeOrRead(
        IDysonWorkspaceFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(fs);
        if (!fs.IsInitialized)
            return Result<(string, bool), string>.AsError("Workspace filesystem is not initialized.");

        var exists = fs.FileExists(ManifestFileName);
        if (exists.IsError)
            return Result<(string, bool), string>.AsError(exists.Error);

        if (exists.Value)
        {
            var text = fs.ReadAllText(ManifestFileName);
            if (text.IsError)
                return Result<(string, bool), string>.AsError(text.Error);
            return Result<(string, bool), string>.AsValue((text.Value, false));
        }

        var json = JsonSerializer.Serialize(CreateDefaultDocument(), JsonOptions);
        var write = fs.WriteAllText(ManifestFileName, json);
        if (write.IsError)
            return Result<(string, bool), string>.AsError(write.Error);

        return Result<(string, bool), string>.AsValue((json, true));
    }

    /// <summary>
    /// GET <paramref name="url"/> via <see cref="SearchHttp"/>; truncates at
    /// <see cref="MaxCharsPerFile"/>. SSRF-guarded.
    /// </summary>
    public static async Task<Result<string, string>> TryFetchUrlBodyAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        if (!IsPathUrl(url))
            return Result<string, string>.AsError("Path is not an http(s) URL.");

        var validation = SearchHttp.ValidateUrl(url);
        if (validation.IsError)
            return Result<string, string>.AsError(validation.Error);

        try
        {
            using var response = await SearchHttp.Client
                .GetAsync(url.Trim(), cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<string, string>.AsError(
                    $"HTTP {(int)response.StatusCode} fetching openrules Path URL.");
            }

            var body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            if (body.Length > MaxCharsPerFile)
                body = body[..MaxCharsPerFile] + $"\n\n(truncated at {MaxCharsPerFile} characters)";

            return Result<string, string>.AsValue(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Failed to fetch openrules Path URL: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the AutoInclude open-rules markdown block for the session system prompt.
    /// Local files only for URL entries (emits a short unfetched note). Prefer
    /// <see cref="BuildSystemPromptBlockAsync(IDysonWorkspaceFileSystem, string?, CancellationToken)"/>.
    /// </summary>
    public static string? BuildSystemPromptBlock(
        IDysonWorkspaceFileSystem fs,
        string? providerId = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        if (!fs.IsInitialized)
            return null;

        var loaded = TryLoad(fs);
        if (loaded.IsError || loaded.Value is null)
            return null;

        return BuildSystemPromptBlockCore(
            loaded.Value,
            fs,
            providerId,
            urlBodyLoader: null);
    }

    /// <summary>
    /// Async AutoInclude block: fetches http(s) Path bodies; local files via FS.
    /// </summary>
    public static async Task<string?> BuildSystemPromptBlockAsync(
        IDysonWorkspaceFileSystem fs,
        string? providerId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fs);
        if (!fs.IsInitialized)
            return null;

        var loaded = TryLoad(fs);
        if (loaded.IsError || loaded.Value is null)
            return null;

        return await BuildSystemPromptBlockCoreAsync(
                loaded.Value,
                fs,
                providerId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a local FS for <paramref name="workDirectoryAbsolutePath"/> and builds the block.
    /// Returns null when path is empty, FS init fails, or there is no open-rules content.
    /// </summary>
    public static async Task<string?> BuildSystemPromptBlockAsync(
        string? workDirectoryAbsolutePath,
        CancellationToken cancellationToken = default) =>
        await BuildSystemPromptBlockAsync(
                workDirectoryAbsolutePath,
                DysonOpenRulesProviders.Dyson,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Creates a local FS for <paramref name="workDirectoryAbsolutePath"/> and builds the block
    /// filtered for <paramref name="providerId"/>.
    /// </summary>
    public static async Task<string?> BuildSystemPromptBlockAsync(
        string? workDirectoryAbsolutePath,
        string? providerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workDirectoryAbsolutePath))
            return null;

        var fsResult = await DysonWorkspaceFileSystems
            .CreateLocalAsync(workDirectoryAbsolutePath, cancellationToken)
            .ConfigureAwait(false);
        if (fsResult.IsError)
            return null;

        return await BuildSystemPromptBlockAsync(fsResult.Value, providerId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>AgentOptional Rules + Skills for skill catalog / LoadSkill resolve.</summary>
    public static IReadOnlyList<DysonOpenRulesResolvedEntry> ListAgentOptional(
        IDysonWorkspaceFileSystem fs,
        string? providerId = null)
    {
        var loaded = TryLoad(fs);
        if (loaded.IsError || loaded.Value is null)
            return [];

        var id = string.IsNullOrWhiteSpace(providerId)
            ? DysonOpenRulesProviders.Dyson
            : providerId;

        return loaded.Value.Rules
            .Concat(loaded.Value.Skills)
            .Where(e => DysonOpenRulesModes.IsAgentOptional(e.Mode) && AppliesToProvider(e, id))
            .ToArray();
    }

    /// <summary>
    /// JSON summary for <c>GetOpenRulesConfig</c> (no file bodies). Returns all manifest rows
    /// (no provider filter). Missing manifest notes the implicit Root default when applicable.
    /// </summary>
    public static string FormatConfigSummaryJson(IDysonWorkspaceFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(fs);
        if (!fs.IsInitialized)
            return """{"error":"Workspace filesystem is not initialized."}""";

        var loaded = TryLoad(fs);
        if (loaded.IsError)
        {
            return JsonSerializer.Serialize(new { error = loaded.Error }, JsonOptions);
        }

        if (loaded.Value is null)
        {
            return JsonSerializer.Serialize(
                new
                {
                    manifestPresent = false,
                    note =
                        $"No {ManifestFileName} and no {DefaultRootPath}; open-rules block omitted.",
                    Root = (string?)null,
                    RootExists = false,
                    Rules = Array.Empty<object>(),
                    Skills = Array.Empty<object>(),
                },
                JsonOptions);
        }

        var config = loaded.Value;
        return JsonSerializer.Serialize(
            new
            {
                manifestPresent = config.ManifestPresent,
                note = config.ManifestPresent
                    ? null
                    : $"No {ManifestFileName}; using implicit Root {DefaultRootPath}.",
                Root = config.RootPath,
                RootExists = config.RootExists,
                Rules = config.Rules.Select(ToSummary).ToArray(),
                Skills = config.Skills.Select(ToSummary).ToArray(),
            },
            JsonOptions);
    }

    /// <summary>Catalog name for an AgentOptional entry (relative path or URL).</summary>
    public static string CatalogNameFor(DysonOpenRulesResolvedEntry entry) =>
        NormalizePath(entry.Path);

    /// <summary>Display name: Description if set, else path stem / last URL segment.</summary>
    public static string CatalogDisplayNameFor(DysonOpenRulesResolvedEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Description))
            return entry.Description.Trim();

        var path = NormalizePath(entry.Path);
        if (entry.IsUrl && Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            var segment = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(segment))
                return segment;
        }

        var stem = Path.GetFileNameWithoutExtension(path.TrimEnd('/'));
        return string.IsNullOrWhiteSpace(stem) ? path : stem;
    }

    /// <summary>
    /// True when <paramref name="name"/> matches an AgentOptional entry by relative path,
    /// stem, URL, or catalog name.
    /// </summary>
    public static bool MatchesAgentOptionalName(DysonOpenRulesResolvedEntry entry, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var trimmed = NormalizePath(name);
        var path = NormalizePath(entry.Path);
        if (string.Equals(path, trimmed, StringComparison.OrdinalIgnoreCase))
            return true;

        if (entry.IsUrl && Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            var segment = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault() ?? "";
            if (!string.IsNullOrWhiteSpace(segment)
                && string.Equals(segment, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var stem = Path.GetFileNameWithoutExtension(path.TrimEnd('/'));
        if (!string.IsNullOrWhiteSpace(stem)
            && string.Equals(stem, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = Path.GetFileName(path);
        if (!string.IsNullOrWhiteSpace(fileName)
            && string.Equals(fileName, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static async Task<string?> BuildSystemPromptBlockCoreAsync(
        DysonOpenRulesConfig config,
        IDysonWorkspaceFileSystem fs,
        string? providerId,
        CancellationToken cancellationToken)
    {
        // Pre-fetch URL AutoInclude bodies that apply to this provider.
        var id = string.IsNullOrWhiteSpace(providerId)
            ? DysonOpenRulesProviders.Dyson
            : providerId;
        var urlBodies = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in config.Rules.Concat(config.Skills)
                     .Where(e => DysonOpenRulesModes.IsAutoInclude(e.Mode)
                         && e.IsUrl
                         && AppliesToProvider(e, id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (urlBodies.ContainsKey(entry.Path))
                continue;
            var fetched = await TryFetchUrlBodyAsync(entry.Path, cancellationToken)
                .ConfigureAwait(false);
            urlBodies[entry.Path] = fetched.IsError ? null : fetched.Value;
            if (fetched.IsError)
                urlBodies[entry.Path + "\0error"] = fetched.Error;
        }

        return BuildSystemPromptBlockCore(
            config,
            fs,
            providerId,
            urlPath =>
            {
                if (urlBodies.TryGetValue(urlPath, out var body) && body is not null)
                    return Result<string, string>.AsValue(body);
                var errKey = urlPath + "\0error";
                var err = urlBodies.TryGetValue(errKey, out var e) && e is not null
                    ? e
                    : "URL body unavailable";
                return Result<string, string>.AsError(err);
            });
    }

    private static string? BuildSystemPromptBlockCore(
        DysonOpenRulesConfig config,
        IDysonWorkspaceFileSystem fs,
        string? providerId,
        Func<string, Result<string, string>>? urlBodyLoader)
    {
        var id = string.IsNullOrWhiteSpace(providerId)
            ? DysonOpenRulesProviders.Dyson
            : providerId;

        var sb = new StringBuilder();
        sb.AppendLine("## OpenRules");
        sb.AppendLine();
        sb.AppendLine(
            "Always-on Root + AutoInclude entries from work-root openrules.json " +
            "(or implicit AGENTS.md). AgentOptional entries are available via LoadSkill / " +
            "composer /skill- and summarized by GetOpenRulesConfig.");
        sb.AppendLine();

        var remaining = MaxTotalChars;
        AppendFileSection(
            sb,
            ref remaining,
            header: $"[OpenRules Root: {config.RootPath}]",
            path: config.RootPath,
            description: null,
            fs,
            exists: config.RootExists,
            isUrl: false,
            urlBodyLoader);

        foreach (var entry in config.Rules.Where(e =>
                     DysonOpenRulesModes.IsAutoInclude(e.Mode) && AppliesToProvider(e, id)))
        {
            AppendFileSection(
                sb,
                ref remaining,
                header: $"[OpenRules AutoInclude Rule: {entry.Path}]",
                path: entry.Path,
                description: entry.Description,
                fs,
                exists: entry.Exists,
                isUrl: entry.IsUrl,
                urlBodyLoader);
            if (remaining <= 0)
                break;
        }

        foreach (var entry in config.Skills.Where(e =>
                     DysonOpenRulesModes.IsAutoInclude(e.Mode) && AppliesToProvider(e, id)))
        {
            AppendFileSection(
                sb,
                ref remaining,
                header: $"[OpenRules AutoInclude Skill: {entry.Path}]",
                path: entry.Path,
                description: entry.Description,
                fs,
                exists: entry.Exists,
                isUrl: entry.IsUrl,
                urlBodyLoader);
            if (remaining <= 0)
                break;
        }

        if (remaining <= 0)
        {
            sb.AppendLine();
            sb.AppendLine(
                $"(OpenRules block truncated at {MaxTotalChars} characters.)");
        }

        var result = sb.ToString().TrimEnd();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static object ToSummary(DysonOpenRulesResolvedEntry e) =>
        new
        {
            e.Path,
            e.Mode,
            e.Description,
            exists = e.Exists,
            isUrl = e.IsUrl,
            Providers = e.Providers is { Count: > 0 } ? e.Providers : null,
        };

    private static Result<IReadOnlyList<DysonOpenRulesResolvedEntry>, string> ResolveEntries(
        IDysonWorkspaceFileSystem fs,
        List<DysonOpenRulesEntryDto>? entries,
        bool isSkill)
    {
        if (entries is null || entries.Count == 0)
            return Result<IReadOnlyList<DysonOpenRulesResolvedEntry>, string>.AsValue([]);

        var list = new List<DysonOpenRulesResolvedEntry>();
        foreach (var raw in entries)
        {
            if (raw is null || string.IsNullOrWhiteSpace(raw.Path))
                continue;

            var mode = string.IsNullOrWhiteSpace(raw.Mode) ? null : raw.Mode.Trim();
            if (!DysonOpenRulesModes.IsKnown(mode))
            {
                return Result<IReadOnlyList<DysonOpenRulesResolvedEntry>, string>.AsError(
                    $"{ManifestFileName}: Mode must be '{DysonOpenRulesModes.AutoInclude}' or " +
                    $"'{DysonOpenRulesModes.AgentOptional}' (got '{raw.Mode}' for Path '{raw.Path}').");
            }

            var isUrl = IsPathUrl(raw.Path);
            var path = NormalizePath(raw.Path);
            bool exists;
            if (isUrl)
            {
                // Well-formed http(s) URL → Exists; do not probe workspace FS.
                exists = true;
            }
            else
            {
                exists = false;
                var existsFile = fs.FileExists(path);
                if (!existsFile.IsError)
                {
                    exists = existsFile.Value;
                    if (!exists)
                    {
                        var existsDir = fs.DirectoryExists(path);
                        if (!existsDir.IsError)
                            exists = existsDir.Value;
                    }
                }
                // Path escape / resolve errors → treat as missing (do not fail session create).
            }

            IReadOnlyList<string>? providers = null;
            if (raw.Providers is { Length: > 0 })
            {
                providers = raw.Providers
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim())
                    .ToArray();
                if (providers.Count == 0)
                    providers = null;
            }

            list.Add(new DysonOpenRulesResolvedEntry
            {
                Path = path,
                Mode = string.Equals(mode, DysonOpenRulesModes.AutoInclude, StringComparison.OrdinalIgnoreCase)
                    ? DysonOpenRulesModes.AutoInclude
                    : DysonOpenRulesModes.AgentOptional,
                Description = string.IsNullOrWhiteSpace(raw.Description) ? null : raw.Description.Trim(),
                Exists = exists,
                IsSkill = isSkill,
                IsUrl = isUrl,
                Providers = providers,
            });
        }

        return Result<IReadOnlyList<DysonOpenRulesResolvedEntry>, string>.AsValue(list);
    }

    private static void AppendFileSection(
        StringBuilder sb,
        ref int remaining,
        string header,
        string path,
        string? description,
        IDysonWorkspaceFileSystem fs,
        bool exists,
        bool isUrl,
        Func<string, Result<string, string>>? urlBodyLoader)
    {
        if (remaining <= 0)
            return;

        sb.AppendLine(header);
        if (!string.IsNullOrWhiteSpace(description))
            sb.AppendLine($"Description: {description.Trim()}");

        if (!exists)
        {
            var warn = $"(missing: {path})";
            sb.AppendLine(warn);
            sb.AppendLine();
            remaining -= header.Length + warn.Length + 8;
            return;
        }

        string body;
        if (isUrl)
        {
            if (urlBodyLoader is null)
            {
                var warn = $"(url not fetched in sync path: {path})";
                sb.AppendLine(warn);
                sb.AppendLine();
                remaining -= header.Length + warn.Length + 8;
                return;
            }

            var fetched = urlBodyLoader(path);
            if (fetched.IsError)
            {
                var warn = $"(unreadable url: {path} — {fetched.Error})";
                sb.AppendLine(warn);
                sb.AppendLine();
                remaining -= header.Length + warn.Length + 8;
                return;
            }

            body = fetched.Value;
        }
        else
        {
            var text = fs.ReadAllText(path);
            if (text.IsError)
            {
                var warn = $"(unreadable: {path} — {text.Error})";
                sb.AppendLine(warn);
                sb.AppendLine();
                remaining -= header.Length + warn.Length + 8;
                return;
            }

            body = text.Value;
            if (body.Length > MaxCharsPerFile)
                body = body[..MaxCharsPerFile] + $"\n\n(truncated at {MaxCharsPerFile} characters)";
        }

        if (body.Length > remaining)
            body = body[..Math.Max(0, remaining)] + $"\n\n(truncated at {MaxTotalChars} total characters)";

        sb.AppendLine(body.TrimEnd());
        sb.AppendLine();
        remaining -= header.Length + body.Length + 8;
    }

    /// <summary>Normalizes work-relative paths; leaves absolute http(s) URLs intact.</summary>
    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim();
        if (IsPathUrl(trimmed))
            return trimmed;
        return trimmed.Replace('\\', '/').TrimStart('/');
    }
}
