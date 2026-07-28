using System.Reflection;
using System.Text;

namespace DysonHarness;

/// <summary>Where a resolved skill was loaded from.</summary>
public enum DysonSkillSource
{
    Included = 0,
    DysonSkills = 1,
    Literal = 2,
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
}

/// <summary>Catalog row for slash search (no body until selected).</summary>
public sealed record DysonSkillCatalogEntry(
    string Name,
    string DisplayName,
    DysonSkillSource Source);

/// <summary>
/// Resolves and loads agent skills: included embedded → <c>.dyson/skills</c> → literal work-relative path.
/// </summary>
public static class DysonSkillLoader
{
    public const string DysonSkillsRelativeDir = ".dyson/skills";

    private static readonly Lazy<IReadOnlyList<IncludedSkill>> IncludedSkills = new(DiscoverIncluded);

    /// <summary>
    /// Resolve order: included (<c>Resources/Skills</c>) → <c>.dyson/skills/{name}</c> → literal work-relative path.
    /// </summary>
    public static Result<DysonLoadedSkill, string> ResolveAndLoad(
        string name,
        bool loadIndexOnly,
        IDysonWorkspaceFileSystem? fs)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result<DysonLoadedSkill, string>.AsError("Skill name is required.");

        var trimmed = name.Trim().Replace('\\', '/');

        var included = TryLoadIncluded(trimmed, loadIndexOnly);
        if (included is not null)
            return Result<DysonLoadedSkill, string>.AsValue(included);

        if (fs is null || !fs.IsInitialized)
        {
            return Result<DysonLoadedSkill, string>.AsError(
                $"Skill '{trimmed}' not found in included skills (workspace filesystem unavailable for .dyson/skills or literal paths).");
        }

        var dyson = TryLoadDysonSkills(trimmed, loadIndexOnly, fs);
        if (dyson.IsError)
            return Result<DysonLoadedSkill, string>.AsError(dyson.Error);
        if (dyson.Value is not null)
            return Result<DysonLoadedSkill, string>.AsValue(dyson.Value);

        var literal = TryLoadLiteral(trimmed, loadIndexOnly, fs);
        if (literal.IsError)
            return Result<DysonLoadedSkill, string>.AsError(literal.Error);
        if (literal.Value is not null)
            return Result<DysonLoadedSkill, string>.AsValue(literal.Value);

        return Result<DysonLoadedSkill, string>.AsError(
            $"Skill '{trimmed}' not found (included → .dyson/skills → literal).");
    }

    /// <summary>Included names plus <c>.dyson/skills/*</c> dirs/files when <paramref name="fs"/> is set.</summary>
    public static IReadOnlyList<DysonSkillCatalogEntry> ListCatalog(IDysonWorkspaceFileSystem? fs)
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
            return list;

        var entries = fs.EnumerateEntries(DysonSkillsRelativeDir);
        if (entries.IsError)
            return list;

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

        return list;
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

    private static Result<DysonLoadedSkill?, string> TryLoadDysonSkills(
        string name,
        bool loadIndexOnly,
        IDysonWorkspaceFileSystem fs)
    {
        var relativeBase = $"{DysonSkillsRelativeDir}/{name.Trim('/')}";
        return LoadPathCandidate(relativeBase, name, loadIndexOnly, fs, DysonSkillSource.DysonSkills);
    }

    private static Result<DysonLoadedSkill?, string> TryLoadLiteral(
        string name,
        bool loadIndexOnly,
        IDysonWorkspaceFileSystem fs) =>
        LoadPathCandidate(name, Path.GetFileNameWithoutExtension(name.TrimEnd('/')), loadIndexOnly, fs, DysonSkillSource.Literal);

    private static Result<DysonLoadedSkill?, string> LoadPathCandidate(
        string relativePath,
        string idHint,
        bool loadIndexOnly,
        IDysonWorkspaceFileSystem fs,
        DysonSkillSource source)
    {
        var fileExists = fs.FileExists(relativePath);
        if (fileExists.IsError)
            return Result<DysonLoadedSkill?, string>.AsError(fileExists.Error);

        if (fileExists.Value)
        {
            var text = fs.ReadAllText(relativePath);
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

        var dirExists = fs.DirectoryExists(relativePath);
        if (dirExists.IsError)
            return Result<DysonLoadedSkill?, string>.AsError(dirExists.Error);
        if (!dirExists.Value)
            return Result<DysonLoadedSkill?, string>.AsValue(null);

        var mdFiles = ListMarkdownFiles(fs, relativePath);
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
            var text = fs.ReadAllText(entry.AbsoluteNativePath);
            if (text.IsError)
                return Result<DysonLoadedSkill?, string>.AsError(text.Error);
            markdown = text.Value;
        }
        else
        {
            var concat = ConcatDirectoryMarkdown(fs, entry, mdFiles.Value);
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

    private static Result<IReadOnlyList<MdFile>, string> ListMarkdownFiles(
        IDysonWorkspaceFileSystem fs,
        string directoryPath)
    {
        var files = fs.EnumerateFiles(directoryPath, "*.md", recursive: true);
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

    private static Result<string, string> ConcatDirectoryMarkdown(
        IDysonWorkspaceFileSystem fs,
        MdFile entry,
        IReadOnlyList<MdFile> all)
    {
        var sb = new StringBuilder();
        var ordered = new List<MdFile> { entry };
        ordered.AddRange(
            all.Where(f => !string.Equals(f.AbsoluteNativePath, entry.AbsoluteNativePath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.RelativeDisplay, StringComparer.OrdinalIgnoreCase));

        foreach (var file in ordered)
        {
            var text = fs.ReadAllText(file.AbsoluteNativePath);
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
