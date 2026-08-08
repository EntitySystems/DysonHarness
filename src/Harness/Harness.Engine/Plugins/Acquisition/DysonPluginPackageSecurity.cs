using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DysonHarness;

public sealed record DysonPluginPackageLimits
{
    public long MaxArchiveBytes { get; init; } = 32 * 1024 * 1024;
    public long MaxExpandedBytes { get; init; } = 128 * 1024 * 1024;
    public long MaxSingleFileBytes { get; init; } = 32 * 1024 * 1024;
    public int MaxEntries { get; init; } = 4096;
    public int MaxPathDepth { get; init; } = 32;
    public int MaxExpansionRatio { get; init; } = 200;
    public TimeSpan PreviewRetention { get; init; } = TimeSpan.FromMinutes(30);
}

internal static class DysonPluginPackageSecurity
{
    private const int CopyBufferSize = 64 * 1024;

    public static Result<string, string> CreatePreviewDirectory(Guid previewId)
    {
        try
        {
            var container = Path.Combine(Path.GetTempPath(), "dyson-plugin-previews", previewId.ToString("N"));
            if (Directory.Exists(container))
                Directory.Delete(container, recursive: true);
            Directory.CreateDirectory(container);
            return Result<string, string>.AsValue(container);
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Failed to create plugin staging directory: {ex.Message}");
        }
    }

    public static Result<string, string> ExtractZip(
        ReadOnlyMemory<byte> bytes,
        string destination,
        DysonPluginPackageLimits limits)
    {
        if (bytes.Length > limits.MaxArchiveBytes)
        {
            return Result<string, string>.AsError(
                $"Plugin archive exceeds the {limits.MaxArchiveBytes}-byte compressed quota.");
        }

        try
        {
            Directory.CreateDirectory(destination);
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.Entries.Count == 0)
                return Result<string, string>.AsError("Plugin archive contains no entries.");
            if (archive.Entries.Count > limits.MaxEntries)
            {
                return Result<string, string>.AsError(
                    $"Plugin archive exceeds the {limits.MaxEntries}-entry quota.");
            }

            var prefix = DetectSingleRootPrefix(archive);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long declaredExpanded = 0;
            long declaredCompressed = 0;

            foreach (var entry in archive.Entries)
            {
                if (IsSymbolicLink(entry))
                    return Result<string, string>.AsError($"Plugin archive link entry is not allowed: '{entry.FullName}'.");

                var normalized = NormalizeArchivePath(entry.FullName, prefix, limits.MaxPathDepth);
                if (normalized.IsError)
                    return Result<string, string>.AsError(normalized.Error);
                if (normalized.Value.Length == 0)
                    continue;

                var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                var collision = ValidateCollision(normalized.Value, isDirectory, seen, files);
                if (collision.IsError)
                    return Result<string, string>.AsError(collision.Error);

                if (isDirectory)
                    continue;

                if (entry.Length > limits.MaxSingleFileBytes)
                {
                    return Result<string, string>.AsError(
                        $"Plugin archive entry '{entry.FullName}' exceeds the single-file quota.");
                }

                declaredExpanded = checked(declaredExpanded + entry.Length);
                declaredCompressed = checked(declaredCompressed + entry.CompressedLength);
                if (declaredExpanded > limits.MaxExpandedBytes)
                    return Result<string, string>.AsError("Plugin archive exceeds the expanded-byte quota.");
            }

            if (declaredExpanded > 1024 * 1024 &&
                declaredExpanded / Math.Max(1, declaredCompressed) > limits.MaxExpansionRatio)
            {
                return Result<string, string>.AsError("Plugin archive exceeds the permitted expansion ratio.");
            }

            long actualExpanded = 0;
            foreach (var entry in archive.Entries)
            {
                var normalized = NormalizeArchivePath(entry.FullName, prefix, limits.MaxPathDepth);
                if (normalized.IsError || normalized.Value.Length == 0)
                    continue;

                var target = ResolveContainedPath(destination, normalized.Value);
                if (target.IsError)
                    return Result<string, string>.AsError(target.Error);

                var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                if (isDirectory)
                {
                    Directory.CreateDirectory(target.Value);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target.Value)!);
                using var input = entry.Open();
                using var output = new FileStream(target.Value, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var copied = CopyWithQuota(input, output, limits.MaxSingleFileBytes, limits.MaxExpandedBytes - actualExpanded);
                if (copied.IsError)
                    return Result<string, string>.AsError(copied.Error);
                actualExpanded += copied.Value;
            }

            var validated = ValidateStagedTree(destination, limits);
            return validated.IsError
                ? Result<string, string>.AsError(validated.Error)
                : Result<string, string>.AsValue(destination);
        }
        catch (InvalidDataException ex)
        {
            return Result<string, string>.AsError($"Plugin package is not a valid ZIP archive: {ex.Message}");
        }
        catch (OverflowException)
        {
            return Result<string, string>.AsError("Plugin archive size metadata overflowed permitted quotas.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<string, string>.AsError($"Failed to stage plugin archive: {ex.Message}");
        }
    }

    public static Result<string, string> CopyFolder(
        string source,
        string destination,
        DysonPluginPackageLimits limits)
    {
        try
        {
            if (!Path.IsPathFullyQualified(source) || !Directory.Exists(source))
                return Result<string, string>.AsError("Local plugin folder must be an existing absolute path.");
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                return Result<string, string>.AsError("Local plugin folder cannot be a link or reparse point.");

            var validation = ValidateStagedTree(source, limits);
            if (validation.IsError)
                return Result<string, string>.AsError(validation.Error);

            Directory.CreateDirectory(destination);
            foreach (var entry in EnumerateTreeWithoutFollowingLinks(source))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    return Result<string, string>.AsError($"Plugin package links/reparse points are not allowed: '{entry.Path}'.");
                var relative = Path.GetRelativePath(source, entry.Path).Replace('\\', '/');
                var target = ResolveContainedPath(destination, relative);
                if (target.IsError)
                    return Result<string, string>.AsError(target.Error);
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(target.Value);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target.Value)!);
                    File.Copy(entry.Path, target.Value, overwrite: false);
                }
            }

            return Result<string, string>.AsValue(destination);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<string, string>.AsError($"Failed to stage local plugin folder: {ex.Message}");
        }
    }

    public static VoidResult<string> ValidateStagedTree(string root, DysonPluginPackageLimits limits)
    {
        try
        {
            if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
                return VoidResult<string>.AsError("Staged plugin package root must be an existing absolute directory.");
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                return VoidResult<string>.AsError("Staged plugin root cannot be a link or reparse point.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            var count = 0;
            foreach (var entry in EnumerateTreeWithoutFollowingLinks(root))
            {
                count++;
                if (count > limits.MaxEntries)
                    return VoidResult<string>.AsError($"Plugin package exceeds the {limits.MaxEntries}-entry quota.");

                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    return VoidResult<string>.AsError($"Plugin package links/reparse points are not allowed: '{entry.Path}'.");

                var relative = Path.GetRelativePath(root, entry.Path).Replace('\\', '/');
                var normalized = ValidateRelativePath(relative, limits.MaxPathDepth);
                if (normalized.IsError)
                    return VoidResult<string>.AsError(normalized.Error);
                var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                var collision = ValidateCollision(normalized.Value, isDirectory, seen, files);
                if (collision.IsError)
                    return collision;

                if (!isDirectory)
                {
                    var length = new FileInfo(entry.Path).Length;
                    if (length > limits.MaxSingleFileBytes)
                        return VoidResult<string>.AsError($"Plugin file '{relative}' exceeds the single-file quota.");
                    total = checked(total + length);
                    if (total > limits.MaxExpandedBytes)
                        return VoidResult<string>.AsError("Plugin package exceeds the expanded-byte quota.");
                }
            }

            return VoidResult<string>.Success;
        }
        catch (OverflowException)
        {
            return VoidResult<string>.AsError("Plugin package size metadata overflowed permitted quotas.");
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Failed to validate staged plugin package: {ex.Message}");
        }
    }

    public static Result<string, string> ComputeTreeChecksum(string root, DysonPluginPackageLimits limits)
    {
        var validation = ValidateStagedTree(root, limits);
        if (validation.IsError)
            return Result<string, string>.AsError(validation.Error);

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var file in EnumerateTreeWithoutFollowingLinks(root)
                         .Where(entry => (entry.Attributes & FileAttributes.Directory) == 0)
                         .Select(entry => entry.Path)
                         .OrderBy(path => Path.GetRelativePath(root, path).Replace('\\', '/'), StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                hash.AppendData(Encoding.UTF8.GetBytes(relative));
                hash.AppendData([0]);
                using var stream = File.OpenRead(file);
                var buffer = new byte[CopyBufferSize];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    hash.AppendData(buffer.AsSpan(0, read));
                hash.AppendData([0]);
            }

            return Result<string, string>.AsValue(
                "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Failed to checksum staged plugin package: {ex.Message}");
        }
    }

    public static Result<string, string> ValidateRelativePath(string path, int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result<string, string>.AsError("Plugin package path is required.");

        var normalized = path.Replace('\\', '/').TrimEnd('/').Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0 || normalized.Length > 4096 || Path.IsPathRooted(normalized) ||
            normalized.StartsWith('/') || normalized.Contains(':') || normalized.Contains("//", StringComparison.Ordinal) ||
            normalized.IndexOf('\0') >= 0)
        {
            return Result<string, string>.AsError($"Unsafe absolute plugin package path: '{path}'.");
        }

        var segments = normalized.Split('/');
        if (segments.Length == 0 || segments.Length > maxDepth || segments.Any(IsUnsafePathSegment))
            return Result<string, string>.AsError($"Unsafe plugin package path: '{path}'.");

        return Result<string, string>.AsValue(string.Join('/', segments));
    }

    public static Result<string, string> ResolveContainedPath(string root, string relativePath)
    {
        var safe = ValidateRelativePath(relativePath, 64);
        if (safe.IsError)
            return Result<string, string>.AsError(safe.Error);

        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot, safe.Value.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = fullRoot + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!fullPath.StartsWith(prefix, comparison))
                return Result<string, string>.AsError($"Plugin package path escapes its root: '{relativePath}'.");
            return Result<string, string>.AsValue(fullPath);
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Invalid plugin package path '{relativePath}': {ex.Message}");
        }
    }

    public static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best-effort staging cleanup */ }
    }

    private static bool IsUnsafePathSegment(string segment)
    {
        if (segment.Length == 0 || segment.Length > 255 || segment is "." or ".." ||
            segment.EndsWith('.') || segment.EndsWith(' ') ||
            segment.Any(ch => char.IsControl(ch) || ch is '<' or '>' or '"' or '|' or '?' or '*'))
        {
            return true;
        }

        var baseName = segment.Split('.', 2)[0];
        return baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               (baseName.Length == 4 &&
                (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                baseName[3] is >= '1' and <= '9');
    }

    private static Result<long, string> CopyWithQuota(Stream input, Stream output, long fileRemaining, long totalRemaining)
    {
        var buffer = new byte[CopyBufferSize];
        long copied = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += read;
            if (copied > fileRemaining || copied > totalRemaining)
                return Result<long, string>.AsError("Plugin archive expanded beyond permitted quotas.");
            output.Write(buffer, 0, read);
        }
        return Result<long, string>.AsValue(copied);
    }

    private static Result<string, string> NormalizeArchivePath(string path, string? prefix, int maxDepth)
    {
        var normalized = path.Replace('\\', '/');
        if (prefix is not null && normalized.StartsWith(prefix, StringComparison.Ordinal))
            normalized = normalized[prefix.Length..];
        normalized = normalized.TrimEnd('/');
        if (normalized.Length == 0)
            return Result<string, string>.AsValue("");
        return ValidateRelativePath(normalized, maxDepth);
    }

    private static string? DetectSingleRootPrefix(ZipArchive archive)
    {
        string? prefix = null;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (name.Length == 0)
                continue;
            var slash = name.IndexOf('/');
            if (slash <= 0)
                return null;
            var firstSegment = name[..slash];
            if (firstSegment is "." or ".." || firstSegment.Contains(':'))
                return null;
            var candidate = name[..(slash + 1)];
            if (prefix is null)
                prefix = candidate;
            else if (!string.Equals(prefix, candidate, StringComparison.Ordinal))
                return null;
        }
        return prefix;
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixMode == 0xA000;
    }

    private static VoidResult<string> ValidateCollision(
        string relative,
        bool isDirectory,
        HashSet<string> seen,
        HashSet<string> files)
    {
        if (!seen.Add(relative))
            return VoidResult<string>.AsError($"Plugin package contains a path collision: '{relative}'.");

        var parent = ParentPath(relative);
        while (parent is not null)
        {
            if (files.Contains(parent))
                return VoidResult<string>.AsError($"Plugin package has a file/directory collision at '{parent}'.");
            parent = ParentPath(parent);
        }

        if (!isDirectory)
        {
            var prefix = relative + "/";
            if (seen.Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                return VoidResult<string>.AsError($"Plugin package has a file/directory collision at '{relative}'.");
            files.Add(relative);
        }

        return VoidResult<string>.Success;
    }

    private static IEnumerable<TreeEntry> EnumerateTreeWithoutFollowingLinks(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var directoryAttributes = File.GetAttributes(directory);
            if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                yield return new TreeEntry(directory, directoryAttributes);
                continue;
            }
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(path);
                yield return new TreeEntry(path, attributes);
                if ((attributes & FileAttributes.Directory) != 0 &&
                    (attributes & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(path);
                }
            }
        }
    }

    private static string? ParentPath(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? null : path[..slash];
    }

    private readonly record struct TreeEntry(string Path, FileAttributes Attributes);
}
