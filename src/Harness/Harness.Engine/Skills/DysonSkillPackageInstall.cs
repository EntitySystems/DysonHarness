using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>
/// Shared zip / SKILL.md install helpers for skill explorer providers.
/// Installs under <see cref="DysonSkillLoader.DysonSkillsRelativeDir"/>/{safeSlug}/.
/// </summary>
internal static class DysonSkillPackageInstall
{
    private static readonly Regex SafeFolderSlug = new(
        @"^[a-zA-Z0-9]([a-zA-Z0-9._-]*[a-zA-Z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const int MaxFolderSlugLength = 128;

    /// <summary>
    /// Converts composite skill ids (e.g. <c>owner/repo/skill</c>) to a filesystem-safe
    /// folder name: replaces path separators with <c>-</c>, keeps alphanumerics / <c>.</c> /
    /// <c>_</c> / <c>-</c>, collapses runs of dashes.
    /// </summary>
    public static Result<string, string> SanitizeFolderSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return Result<string, string>.AsError("slug is required.");

        var trimmed = slug.Trim();
        var sb = new StringBuilder(trimmed.Length);
        var lastDash = false;
        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_')
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (ch is '/' or '\\' or '-' or ' ')
            {
                if (sb.Length == 0 || lastDash)
                    continue;
                sb.Append('-');
                lastDash = true;
            }
            // drop other characters
        }

        while (sb.Length > 0 && sb[^1] is '-' or '.')
            sb.Length--;

        var safe = sb.ToString();
        if (safe.Length == 0 || safe.Length > MaxFolderSlugLength || !SafeFolderSlug.IsMatch(safe))
            return Result<string, string>.AsError($"Invalid skill slug '{trimmed}'.");

        return Result<string, string>.AsValue(safe);
    }

    /// <summary>
    /// From a repo zipball, finds a folder named <paramref name="skillFolderName"/> that contains
    /// <c>SKILL.md</c> and returns a new zip of that folder's contents (files at archive root).
    /// When multiple matches exist, the shallowest path wins.
    /// </summary>
    public static Result<byte[], string> FilterZipToNamedSkillFolder(byte[] zipBytes, string skillFolderName)
    {
        ArgumentNullException.ThrowIfNull(zipBytes);

        if (string.IsNullOrWhiteSpace(skillFolderName))
            return Result<byte[], string>.AsError("skill folder name is required.");

        var folder = skillFolderName.Trim();
        if (folder.Contains('/') || folder.Contains('\\') || folder is "." or ".." || !IsSafeRelativeZipPath(folder))
            return Result<byte[], string>.AsError($"Invalid skill folder name '{skillFolderName}'.");

        try
        {
            using var ms = new MemoryStream(zipBytes, writable: false);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
            var prefix = DetectSingleRootPrefix(archive);

            string? skillRoot = null;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                    continue;

                var relative = NormalizeZipEntryPath(entry.FullName, prefix);
                if (relative is null
                    || !relative.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var dir = relative[..^"SKILL.md".Length].TrimEnd('/');
                var slash = dir.LastIndexOf('/');
                var name = slash < 0 ? dir : dir[(slash + 1)..];
                if (!name.Equals(folder, StringComparison.OrdinalIgnoreCase))
                    continue;

                var root = dir + "/";
                if (skillRoot is null || root.Length < skillRoot.Length)
                    skillRoot = root;
            }

            if (skillRoot is null)
            {
                return Result<byte[], string>.AsError(
                    $"Skill folder '{folder}' with SKILL.md was not found in the package.");
            }

            using var outMs = new MemoryStream();
            using (var outZip = new ZipArchive(outMs, ZipArchiveMode.Create, leaveOpen: true))
            {
                var wrote = false;
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith('/'))
                        continue;

                    var relative = NormalizeZipEntryPath(entry.FullName, prefix);
                    if (relative is null
                        || !relative.StartsWith(skillRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var inner = relative[skillRoot.Length..];
                    if (inner.Length == 0 || !IsSafeRelativeZipPath(inner))
                        continue;

                    var outEntry = outZip.CreateEntry(inner);
                    using var src = entry.Open();
                    using var dest = outEntry.Open();
                    src.CopyTo(dest);
                    wrote = true;
                }

                if (!wrote)
                {
                    return Result<byte[], string>.AsError(
                        $"Skill folder '{folder}' contained no files.");
                }
            }

            return Result<byte[], string>.AsValue(outMs.ToArray());
        }
        catch (InvalidDataException ex)
        {
            return Result<byte[], string>.AsError("Skill package is not a valid zip: " + ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<byte[], string>.AsError("Failed to extract skill folder from package: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Reads SKILL.md from zip bytes in memory (strips a single top-level folder when present).
    /// </summary>
    public static Result<string, string> ReadSkillMarkdownFromZip(byte[] zipBytes)
    {
        ArgumentNullException.ThrowIfNull(zipBytes);

        try
        {
            using var ms = new MemoryStream(zipBytes, writable: false);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
            var skillMd = FindSkillMarkdownEntry(archive);
            if (skillMd is null)
                return Result<string, string>.AsError("Package does not contain SKILL.md.");

            using var stream = skillMd.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            return Result<string, string>.AsValue(text);
        }
        catch (InvalidDataException ex)
        {
            return Result<string, string>.AsError("Skill package is not a valid zip: " + ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<string, string>.AsError("Failed to preview skill package: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Extracts a skill zip into <c>.dyson/skills/{safeSlug}/</c>, stripping a single top-level
    /// folder when present. Requires SKILL.md after extract.
    /// </summary>
    public static async Task<Result<string, string>> ExtractZipToSkillDirAsync(
        byte[] zipBytes,
        string safeSlug,
        IDysonWorkspaceFileSystem fs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zipBytes);
        ArgumentNullException.ThrowIfNull(fs);

        var slugResult = RequireSafeFolderSlug(safeSlug);
        if (slugResult.IsError)
            return Result<string, string>.AsError(slugResult.Error);

        var destRoot = DysonSkillLoader.DysonSkillsRelativeDir + "/" + slugResult.Value;

        try
        {
            var prepared = await PrepareSkillDirAsync(destRoot, fs, cancellationToken).ConfigureAwait(false);
            if (prepared.IsError)
                return Result<string, string>.AsError(prepared.Error);

            using var ms = new MemoryStream(zipBytes, writable: false);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
            var prefix = DetectSingleRootPrefix(archive);

            var wroteFile = false;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeZipEntryPath(entry.FullName, prefix);
                if (relative is null)
                    continue;

                if (relative.Length == 0 || relative.EndsWith('/'))
                {
                    var dirRel = relative.TrimEnd('/');
                    if (dirRel.Length == 0)
                        continue;
                    if (!IsSafeRelativeZipPath(dirRel))
                    {
                        return Result<string, string>.AsError(
                            $"Refusing to extract unsafe zip path '{entry.FullName}'.");
                    }
                    var dirCreate = await fs.CreateDirectoryAsync(destRoot + "/" + dirRel, cancellationToken)
                        .ConfigureAwait(false);
                    if (dirCreate.IsError)
                        return Result<string, string>.AsError(dirCreate.Error);
                    continue;
                }

                if (!IsSafeRelativeZipPath(relative))
                {
                    return Result<string, string>.AsError(
                        $"Refusing to extract unsafe zip path '{entry.FullName}'.");
                }

                var target = destRoot + "/" + relative;
                var parentSlash = target.LastIndexOf('/');
                if (parentSlash > 0)
                {
                    var parent = target[..parentSlash];
                    var parentCreate = await fs.CreateDirectoryAsync(parent, cancellationToken)
                        .ConfigureAwait(false);
                    if (parentCreate.IsError)
                        return Result<string, string>.AsError(parentCreate.Error);
                }

                using var entryStream = entry.Open();
                using var outMs = new MemoryStream();
                entryStream.CopyTo(outMs);
                var write = await fs.WriteAllBytesAsync(target, outMs.ToArray(), cancellationToken)
                    .ConfigureAwait(false);
                if (write.IsError)
                    return Result<string, string>.AsError(write.Error);
                wroteFile = true;
            }

            if (!wroteFile)
                return Result<string, string>.AsError("Skill package contained no files.");

            var skillMd = await fs.FileExistsAsync(destRoot + "/SKILL.md", cancellationToken)
                .ConfigureAwait(false);
            if (skillMd.IsError)
                return Result<string, string>.AsError(skillMd.Error);
            if (!skillMd.Value)
            {
                // ponytail: some zips use skill.md; accept case variants via enumerate
                if (!await HasSkillMarkdownAsync(fs, destRoot, cancellationToken).ConfigureAwait(false))
                {
                    return Result<string, string>.AsError(
                        $"Installed package under '{destRoot}' is missing SKILL.md.");
                }
            }

            return Result<string, string>.AsValue(destRoot);
        }
        catch (InvalidDataException ex)
        {
            return Result<string, string>.AsError("Skill package is not a valid zip: " + ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<string, string>.AsError("Failed to extract skill package: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Writes a single SKILL.md body to <c>.dyson/skills/{safeSlug}/SKILL.md</c>
    /// (markdown-only install, e.g. SkillsHub).
    /// </summary>
    public static async Task<Result<string, string>> WriteSkillMarkdownAsync(
        string markdown,
        string safeSlug,
        IDysonWorkspaceFileSystem fs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(fs);

        var slugResult = RequireSafeFolderSlug(safeSlug);
        if (slugResult.IsError)
            return Result<string, string>.AsError(slugResult.Error);

        var destRoot = DysonSkillLoader.DysonSkillsRelativeDir + "/" + slugResult.Value;
        var prepared = await PrepareSkillDirAsync(destRoot, fs, cancellationToken).ConfigureAwait(false);
        if (prepared.IsError)
            return Result<string, string>.AsError(prepared.Error);

        var write = await fs.WriteAllTextAsync(destRoot + "/SKILL.md", markdown, cancellationToken)
            .ConfigureAwait(false);
        if (write.IsError)
            return Result<string, string>.AsError(write.Error);

        return Result<string, string>.AsValue(destRoot);
    }

    internal static string? DetectSingleRootPrefix(ZipArchive archive)
    {
        string? root = null;
        var sawEntry = false;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.Length == 0)
                continue;
            sawEntry = true;

            var slash = name.IndexOf('/');
            if (slash <= 0)
                return null;

            var firstSeg = name[..slash];
            if (firstSeg is "." or ".." || firstSeg.Contains(':'))
                return null;

            var first = name[..(slash + 1)];
            if (root is null)
                root = first;
            else if (!string.Equals(root, first, StringComparison.Ordinal))
                return null;
        }

        return sawEntry ? root : null;
    }

    internal static bool IsSafeRelativeZipPath(string relative)
    {
        if (string.IsNullOrEmpty(relative)
            || Path.IsPathRooted(relative)
            || relative.Contains(':')
            || relative.StartsWith('/')
            || relative.StartsWith('\\'))
        {
            return false;
        }

        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
                return false;
        }

        return true;
    }

    internal static ZipArchiveEntry? FindSkillMarkdownEntry(ZipArchive archive)
    {
        var prefix = DetectSingleRootPrefix(archive);
        ZipArchiveEntry? nested = null;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;

            var relative = NormalizeZipEntryPath(entry.FullName, prefix);
            if (relative is null)
                continue;

            if (string.Equals(relative, "SKILL.md", StringComparison.OrdinalIgnoreCase))
                return entry;

            if (nested is null
                && relative.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                nested = entry;
            }
        }

        return nested;
    }

    private static Result<string, string> RequireSafeFolderSlug(string safeSlug)
    {
        if (string.IsNullOrWhiteSpace(safeSlug))
            return Result<string, string>.AsError("slug is required.");

        var trimmed = safeSlug.Trim();
        if (trimmed.Length > MaxFolderSlugLength || !SafeFolderSlug.IsMatch(trimmed))
            return Result<string, string>.AsError($"Invalid skill slug '{trimmed}'.");

        return Result<string, string>.AsValue(trimmed);
    }

    private static async Task<Result<string, string>> PrepareSkillDirAsync(
        string destRoot,
        IDysonWorkspaceFileSystem fs,
        CancellationToken cancellationToken)
    {
        if (!fs.IsInitialized)
            return Result<string, string>.AsError("Workspace filesystem is not initialized.");

        var exists = await fs.DirectoryExistsAsync(destRoot, cancellationToken).ConfigureAwait(false);
        if (exists.IsError)
            return Result<string, string>.AsError(exists.Error);
        if (exists.Value)
        {
            var deleted = await fs.DeleteDirectoryAsync(destRoot, recursive: true, cancellationToken)
                .ConfigureAwait(false);
            if (deleted.IsError)
                return Result<string, string>.AsError(deleted.Error);
        }

        var created = await fs.CreateDirectoryAsync(destRoot, cancellationToken).ConfigureAwait(false);
        if (created.IsError)
            return Result<string, string>.AsError(created.Error);

        return Result<string, string>.AsValue(destRoot);
    }

    private static string? NormalizeZipEntryPath(string fullName, string? prefix)
    {
        var name = fullName.Replace('\\', '/');
        if (name.StartsWith('/'))
            name = name[1..];

        if (prefix is not null)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                return null;
            name = name[prefix.Length..];
        }

        return name;
    }

    private static async Task<bool> HasSkillMarkdownAsync(
        IDysonWorkspaceFileSystem fs,
        string destRoot,
        CancellationToken cancellationToken)
    {
        var entries = await fs.EnumerateEntriesAsync(destRoot, cancellationToken).ConfigureAwait(false);
        if (entries.IsError)
            return false;
        return entries.Value.Any(e =>
            !e.IsDirectory && string.Equals(e.Name, "SKILL.md", StringComparison.OrdinalIgnoreCase));
    }
}
