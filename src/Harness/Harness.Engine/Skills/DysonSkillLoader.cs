using System.Reflection;
using System.Text;

namespace DysonHarness;

/// <summary>Where a resolved skill was loaded from.</summary>
public enum DysonSkillSource
{
    Included = 0,
    DysonSkills = 1,
    Literal = 2,
    OpenRules = 3,
    Plugin = 4,
}

/// <summary>Loaded skill payload for model injection + UI modal.</summary>
public sealed class DysonLoadedSkill
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string ResolvedPath { get; init; }
    public required string Markdown { get; init; }
    public required DysonSkillSource Source { get; init; }
    public required bool LoadIndexOnly { get; init; }
    /// <summary>Normalized plugin id when this skill came from a plugin package.</summary>
    public string? PluginId { get; init; }
    /// <summary>Package-relative entry path when this skill came from a plugin package.</summary>
    public string? PluginPackageRelativePath { get; init; }
}

/// <summary>Catalog row for slash search (no body until selected).</summary>
public sealed record DysonSkillCatalogEntry(
    string Name,
    string DisplayName,
    DysonSkillSource Source,
    string? PluginId = null,
    string? PluginPackageRelativePath = null);

/// <summary>
/// Resolves and loads agent skills: included embedded → <c>.dyson/skills</c> → literal
/// work-relative path → openrules <c>AgentOptional</c> Rules/Skills.
/// </summary>
public static class DysonSkillLoader
{
    public const string DysonSkillsRelativeDir = ".dyson/skills";

    private static readonly Lazy<IReadOnlyList<IncludedSkill>> IncludedSkills = new(DiscoverIncluded);

    /// <summary>
    /// Async resolve/load (fetches openrules http(s) AgentOptional Path bodies when needed).
    /// Resolve order: included (<c>Resources/Skills</c>) → <c>.dyson/skills/{name}</c> →
    /// literal work-relative path → openrules AgentOptional (by path / stem / catalog name).
    /// AutoInclude openrules entries are system-prompt only (not listed here).
    /// OpenRules URL Paths require <see cref="ResolveAndLoadAsync"/>.
    /// </summary>
    public static Task<Result<DysonLoadedSkill, string>> ResolveAndLoadAsync(
        string name,
        bool loadIndexOnly,
        IDysonWorkspaceFileSystem? fs,
        CancellationToken cancellationToken = default,
        DysonPluginContributionSet? pluginContributions = null) =>
        ResolveAndLoadAsync(name, loadIndexOnly, fs, providerId: null, cancellationToken, pluginContributions);

    /// <summary>
    /// Async resolve/load filtered by <paramref name="providerId"/> (default dyson).
    /// </summary>
    public static async Task<Result<DysonLoadedSkill, string>> ResolveAndLoadAsync(
        string name,
        bool loadIndexOnly,
        IDysonWorkspaceFileSystem? fs,
        string? providerId,
        CancellationToken cancellationToken = default,
        DysonPluginContributionSet? pluginContributions = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result<DysonLoadedSkill, string>.AsError("Skill name is required.");

        var trimmed = name.Trim().Replace('\\', '/');
        var early = await TryResolveBeforeOpenRulesAsync(trimmed, loadIndexOnly, fs, cancellationToken)
            .ConfigureAwait(false);
        if (early.IsError)
            return Result<DysonLoadedSkill, string>.AsError(early.Error);
        if (early.Value is not null)
            return Result<DysonLoadedSkill, string>.AsValue(early.Value);

        if (fs is not null && fs.IsInitialized)
        {
            var openRules = await TryLoadOpenRulesAgentOptionalAsync(
                    trimmed, loadIndexOnly, fs, providerId, cancellationToken)
                .ConfigureAwait(false);
            if (openRules.IsError)
                return Result<DysonLoadedSkill, string>.AsError(openRules.Error);
            if (openRules.Value is not null)
                return Result<DysonLoadedSkill, string>.AsValue(openRules.Value);
        }

        var plugin = TryLoadPluginSkill(trimmed, loadIndexOnly, pluginContributions);
        if (plugin.IsError)
            return Result<DysonLoadedSkill, string>.AsError(plugin.Error);
        if (plugin.Value is not null)
            return Result<DysonLoadedSkill, string>.AsValue(plugin.Value);

        return Result<DysonLoadedSkill, string>.AsError(
            $"Skill '{trimmed}' not found (included → .dyson/skills → literal → openrules AgentOptional → plugin).");
    }

    /// <summary>
    /// Included → .dyson/skills → literal. Null value means fall through to openrules.
    /// </summary>
    private static async Task<Result<DysonLoadedSkill?, string>> TryResolveBeforeOpenRulesAsync(
        string trimmed,
        bool loadIndexOnly,
        IDysonWorkspaceFileSystem? fs,
        CancellationToken cancellationToken)
    {
        var included = TryLoadIncluded(trimmed, loadIndexOnly);
        if (included is not null)
            return Result<DysonLoadedSkill?, string>.AsValue(included);

        if (fs is null || !fs.IsInitialized)
            return Result<DysonLoadedSkill?, string>.AsValue(null);

        var dyson = await TryLoadDysonSkillsAsync(trimmed, loadIndexOnly, fs, cancellationToken)
            .ConfigureAwait(false);
        if (dyson.IsError)
            return Result<DysonLoadedSkill?, string>.AsError(dyson.Error);
        if (dyson.Value is not null)
            return Result<DysonLoadedSkill?, string>.AsValue(dyson.Value);

        var literal = await TryLoadLiteralAsync(trimmed, loadIndexOnly, fs, cancellationToken)
            .ConfigureAwait(false);
        if (literal.IsError)
            return Result<DysonLoadedSkill?, string>.AsError(literal.Error);
        if (literal.Value is not null)
            return Result<DysonLoadedSkill?, string>.AsValue(literal.Value);

        return Result<DysonLoadedSkill?, string>.AsValue(null);
    }

    /// <summary>
    /// Included names plus <c>.dyson/skills/*</c> plus openrules <c>AgentOptional</c> entries
    /// when <paramref name="fs"/> is set (provider-filtered; default dyson).
    /// </summary>
    public static async Task<IReadOnlyList<DysonSkillCatalogEntry>> ListCatalogAsync(
        IDysonWorkspaceFileSystem? fs,
        string? providerId = null,
        CancellationToken cancellationToken = default,
        DysonPluginContributionSet? pluginContributions = null)
    {
        var list = new List<DysonSkillCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var inc in IncludedSkills.Value)
        {
            if (!seen.Add(inc.Stem))
                continue;
            list.Add(new DysonSkillCatalogEntry(inc.Stem, inc.Stem, DysonSkillSource.Included));
        }

        if (fs is null || !fs.IsInitialized)
        {
            AddPluginCatalogEntries(list, seen, pluginContributions);
            return list;
        }

        var entries = await fs.EnumerateEntriesAsync(DysonSkillsRelativeDir, cancellationToken)
            .ConfigureAwait(false);
        if (!entries.IsError)
        {
            foreach (var entry in entries.Value.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                var stem = entry.IsDirectory
                    ? entry.Name
                    : Path.GetFileNameWithoutExtension(entry.Name);
                if (string.IsNullOrWhiteSpace(stem) || !seen.Add(stem))
                    continue;

                if (!entry.IsDirectory
                    && !entry.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                list.Add(new DysonSkillCatalogEntry(stem, stem, DysonSkillSource.DysonSkills));
            }
        }

        var optional = await DysonOpenRules
            .ListAgentOptionalAsync(fs, providerId, cancellationToken)
            .ConfigureAwait(false);
        foreach (var entry in optional)
        {
            var name = DysonOpenRules.CatalogNameFor(entry);
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                continue;

            list.Add(new DysonSkillCatalogEntry(
                name,
                DysonOpenRules.CatalogDisplayNameFor(entry),
                DysonSkillSource.OpenRules));
        }

        AddPluginCatalogEntries(list, seen, pluginContributions);
        return list;
    }

    private static Result<DysonLoadedSkill?, string> TryLoadPluginSkill(
        string name,
        bool loadIndexOnly,
        DysonPluginContributionSet? pluginContributions)
    {
        if (pluginContributions is null)
            return Result<DysonLoadedSkill?, string>.AsValue(null);

        var match = pluginContributions.Skills.FirstOrDefault(skill =>
            string.Equals(skill.StableId, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return Result<DysonLoadedSkill?, string>.AsValue(null);

        var loaded = new DysonPluginContributionResolver().LoadSkill(pluginContributions, match.StableId, loadIndexOnly);
        return loaded.IsError
            ? Result<DysonLoadedSkill?, string>.AsError(loaded.Error)
            : Result<DysonLoadedSkill?, string>.AsValue(loaded.Value);
    }

    private static void AddPluginCatalogEntries(
        List<DysonSkillCatalogEntry> list,
        HashSet<string> seen,
        DysonPluginContributionSet? pluginContributions)
    {
        if (pluginContributions is null)
            return;

        foreach (var skill in pluginContributions.Skills.OrderBy(item => item.StableId, StringComparer.Ordinal))
        {
            if (!seen.Add(skill.StableId))
                continue;
            list.Add(new DysonSkillCatalogEntry(
                skill.StableId,
                skill.DisplayName,
                DysonSkillSource.Plugin,
                skill.Provenance.PluginId,
                skill.Provenance.PackageRelativePath));
        }
    }

    private static async Task<Result<DysonLoadedSkill?, string>> TryLoadOpenRulesAgentOptionalAsync(
        string name,
        bool loadIndexOnly,
        IDysonWorkspaceFileSystem fs,
        string? providerId,
        CancellationToken cancellationToken)
    {
        var optional = await DysonOpenRules
            .ListAgentOptionalAsync(fs, providerId, cancellationToken)
            .ConfigureAwait(false);
        var match = optional.FirstOrDefault(e => DysonOpenRules.MatchesAgentOptionalName(e, name));
        if (match is null)
            return Result<DysonLoadedSkill?, string>.AsValue(null);

        if (match.IsUrl)
        {
            var fetched = await DysonOpenRules
                .TryFetchUrlBodyAsync(match.Path, cancellationToken)
                .ConfigureAwait(false);
            if (fetched.IsError)
                return Result<DysonLoadedSkill?, string>.AsError(fetched.Error);

            var display = !string.IsNullOrWhiteSpace(match.Description)
                ? match.Description.Trim()
                : DysonOpenRules.CatalogDisplayNameFor(match);

            return Result<DysonLoadedSkill?, string>.AsValue(new DysonLoadedSkill
            {
                Id = display,
                DisplayName = display,
                ResolvedPath = match.Path,
                Markdown = fetched.Value,
                Source = DysonSkillSource.OpenRules,
                LoadIndexOnly = loadIndexOnly,
            });
        }

        return await FinishOpenRulesLocalLoadAsync(match, loadIndexOnly, fs, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Result<DysonLoadedSkill?, string>> FinishOpenRulesLocalLoadAsync(
        DysonOpenRulesResolvedEntry match,
        bool loadIndexOnly,
        IDysonWorkspaceFileSystem fs,
        CancellationToken cancellationToken)
    {
        // Reuse path loader; for single files loadIndexOnly is ignored (same as Literal).
        var loaded = await LoadPathCandidateAsync(
                match.Path,
                Path.GetFileNameWithoutExtension(match.Path.TrimEnd('/')),
                loadIndexOnly,
                fs,
                DysonSkillSource.OpenRules,
                cancellationToken)
            .ConfigureAwait(false);
        if (loaded.IsError)
            return Result<DysonLoadedSkill?, string>.AsError(loaded.Error);
        if (loaded.Value is null)
        {
            return Result<DysonLoadedSkill?, string>.AsError(
                $"OpenRules AgentOptional path missing: {match.Path}");
        }

        if (!string.IsNullOrWhiteSpace(match.Description))
        {
            return Result<DysonLoadedSkill?, string>.AsValue(new DysonLoadedSkill
            {
                Id = loaded.Value.Id,
                DisplayName = match.Description.Trim(),
                ResolvedPath = loaded.Value.ResolvedPath,
                Markdown = loaded.Value.Markdown,
                Source = DysonSkillSource.OpenRules,
                LoadIndexOnly = loaded.Value.LoadIndexOnly,
            });
        }

        return Result<DysonLoadedSkill?, string>.AsValue(loaded.Value);
    }

    private static DysonLoadedSkill? TryLoadIncluded(string name, bool loadIndexOnly)
    {
        foreach (var skill in IncludedSkills.Value)
        {
            if (!MatchesIncluded(skill, name))
                continue;

            // Included skills are single markdown files — index and full are the same.
            return new DysonLoadedSkill
            {
                Id = skill.Stem,
                DisplayName = skill.Stem,
                ResolvedPath = $"Resources/Skills/{skill.FileName}",
                Markdown = skill.Markdown,
                Source = DysonSkillSource.Included,
                LoadIndexOnly = loadIndexOnly,
            };
        }

        return null;
    }

    private static bool MatchesIncluded(IncludedSkill skill, string name)
    {
        if (string.Equals(skill.Stem, name, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(skill.FileName, name, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static Task<Result<DysonLoadedSkill?, string>> TryLoadDysonSkillsAsync(
        string name,
        bool loadIndexOnly,
        IDysonWorkspaceFileSystem fs,
        CancellationToken cancellationToken)
    {
        var relativeBase = $"{DysonSkillsRelativeDir}/{name.Trim('/')}";
        return LoadPathCandidateAsync(
            relativeBase, name, loadIndexOnly, fs, DysonSkillSource.DysonSkills, cancellationToken);
    }

    private static Task<Result<DysonLoadedSkill?, string>> TryLoadLiteralAsync(
        string name,
        bool loadIndexOnly,
        IDysonWorkspaceFileSystem fs,
        CancellationToken cancellationToken) =>
        LoadPathCandidateAsync(
            name,
            Path.GetFileNameWithoutExtension(name.TrimEnd('/')),
            loadIndexOnly,
            fs,
            DysonSkillSource.Literal,
            cancellationToken);

    private static async Task<Result<DysonLoadedSkill?, string>> LoadPathCandidateAsync(
        string relativePath,
        string idHint,
        bool loadIndexOnly,
        IDysonWorkspaceFileSystem fs,
        DysonSkillSource source,
        CancellationToken cancellationToken)
    {
        var fileExists = await fs.FileExistsAsync(relativePath, cancellationToken).ConfigureAwait(false);
        if (fileExists.IsError)
            return Result<DysonLoadedSkill?, string>.AsError(fileExists.Error);

        if (fileExists.Value)
        {
            var text = await fs.ReadAllTextAsync(relativePath, cancellationToken).ConfigureAwait(false);
            if (text.IsError)
                return Result<DysonLoadedSkill?, string>.AsError(text.Error);

            var display = Path.GetFileNameWithoutExtension(relativePath.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(display))
                display = idHint;

            var rel = fs.GetRelativePath(relativePath);
            var resolved = rel.IsError ? relativePath.Replace('\\', '/') : rel.Value;

            return Result<DysonLoadedSkill?, string>.AsValue(new DysonLoadedSkill
            {
                Id = display,
                DisplayName = display,
                ResolvedPath = resolved,
                Markdown = text.Value,
                Source = source,
                LoadIndexOnly = loadIndexOnly,
            });
        }

        var dirExists = await fs.DirectoryExistsAsync(relativePath, cancellationToken).ConfigureAwait(false);
        if (dirExists.IsError)
            return Result<DysonLoadedSkill?, string>.AsError(dirExists.Error);
        if (!dirExists.Value)
            return Result<DysonLoadedSkill?, string>.AsValue(null);

        var mdFiles = await ListMarkdownFilesAsync(fs, relativePath, cancellationToken).ConfigureAwait(false);
        if (mdFiles.IsError)
            return Result<DysonLoadedSkill?, string>.AsError(mdFiles.Error);
        if (mdFiles.Value.Count == 0)
        {
            return Result<DysonLoadedSkill?, string>.AsError(
                $"Skill directory '{relativePath}' has no .md files.");
        }

        var entry = PickEntryFile(mdFiles.Value);
        string markdown;
        if (loadIndexOnly)
        {
            var text = await fs.ReadAllTextAsync(entry.AbsoluteNativePath, cancellationToken)
                .ConfigureAwait(false);
            if (text.IsError)
                return Result<DysonLoadedSkill?, string>.AsError(text.Error);
            markdown = text.Value;
        }
        else
        {
            var concat = await ConcatDirectoryMarkdownAsync(fs, entry, mdFiles.Value, cancellationToken)
                .ConfigureAwait(false);
            if (concat.IsError)
                return Result<DysonLoadedSkill?, string>.AsError(concat.Error);
            markdown = concat.Value;
        }

        var dirRel = fs.GetRelativePath(relativePath);
        var resolvedDir = dirRel.IsError ? relativePath.Replace('\\', '/') : dirRel.Value;
        var id = Path.GetFileName(resolvedDir.TrimEnd('/'));
        if (string.IsNullOrWhiteSpace(id))
            id = idHint;

        return Result<DysonLoadedSkill?, string>.AsValue(new DysonLoadedSkill
        {
            Id = id,
            DisplayName = id,
            ResolvedPath = resolvedDir,
            Markdown = markdown,
            Source = source,
            LoadIndexOnly = loadIndexOnly,
        });
    }

    private sealed record MdFile(string AbsoluteNativePath, string RelativeDisplay, string FileName);

    private static async Task<Result<IReadOnlyList<MdFile>, string>> ListMarkdownFilesAsync(
        IDysonWorkspaceFileSystem fs,
        string directoryPath,
        CancellationToken cancellationToken)
    {
        var files = await fs.EnumerateFilesAsync(directoryPath, "*.md", recursive: true, cancellationToken)
            .ConfigureAwait(false);
        if (files.IsError)
            return Result<IReadOnlyList<MdFile>, string>.AsError(files.Error);

        var list = new List<MdFile>();
        foreach (var abs in files.Value)
        {
            var rel = fs.GetRelativePath(abs);
            if (rel.IsError)
                continue;
            var fileName = Path.GetFileName(abs);
            list.Add(new MdFile(abs, rel.Value, fileName));
        }

        return Result<IReadOnlyList<MdFile>, string>.AsValue(list);
    }

    private static MdFile PickEntryFile(IReadOnlyList<MdFile> files)
    {
        var skillMd = files.FirstOrDefault(f =>
            string.Equals(f.FileName, "SKILL.md", StringComparison.OrdinalIgnoreCase));
        if (skillMd is not null)
            return skillMd;

        return files
            .OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static async Task<Result<string, string>> ConcatDirectoryMarkdownAsync(
        IDysonWorkspaceFileSystem fs,
        MdFile entry,
        IReadOnlyList<MdFile> all,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var ordered = new List<MdFile> { entry };
        ordered.AddRange(
            all.Where(f => !string.Equals(f.AbsoluteNativePath, entry.AbsoluteNativePath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.RelativeDisplay, StringComparer.OrdinalIgnoreCase));

        foreach (var file in ordered)
        {
            var text = await fs.ReadAllTextAsync(file.AbsoluteNativePath, cancellationToken)
                .ConfigureAwait(false);
            if (text.IsError)
                return Result<string, string>.AsError(text.Error);

            if (sb.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }

            sb.Append("<!-- ");
            sb.Append(file.RelativeDisplay);
            sb.AppendLine(" -->");
            sb.AppendLine();
            sb.Append(text.Value.TrimEnd());
            sb.AppendLine();
        }

        return Result<string, string>.AsValue(sb.ToString());
    }

    private sealed record IncludedSkill(string Stem, string FileName, string Markdown);

    private static IReadOnlyList<IncludedSkill> DiscoverIncluded()
    {
        var asm = typeof(DysonSkillLoader).Assembly;
        var list = new List<IncludedSkill>();

        foreach (var resourceName in asm.GetManifestResourceNames())
        {
            if (!resourceName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                continue;
            if (resourceName.IndexOf("Resources.Skills", StringComparison.OrdinalIgnoreCase) < 0
                && resourceName.IndexOf(".Skills.", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null)
                continue;

            using var reader = new StreamReader(stream);
            var markdown = reader.ReadToEnd();

            // Resource names look like Harness.Engine.Resources.Skills.JDSL.md
            var segments = resourceName.Split('.');
            if (segments.Length < 2)
                continue;
            var stem = segments[^2];
            if (string.IsNullOrWhiteSpace(stem))
                continue;

            list.Add(new IncludedSkill(stem, $"{stem}.md", markdown));
        }

        return list
            .OrderBy(s => s.Stem, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
