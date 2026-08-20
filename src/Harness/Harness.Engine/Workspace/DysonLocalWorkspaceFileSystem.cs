using System.Text;

namespace DysonHarness;

/// <summary>
/// Path-based workspace FS over a local directory, mapped drive, or UNC/SMB mount
/// (including Azure Files mounts). Call <see cref="InitializeAsync"/> with
/// <see cref="DysonWorkspaceSubjects.LocalFs"/> before IO.
/// </summary>
public sealed class DysonLocalWorkspaceFileSystem : IDysonWorkspaceFileSystem
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _root;
    private string? _subjectId;
    private bool _initialized;

    public DysonLocalWorkspaceFileSystem(string absoluteRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteRootPath);
        _root = Path.GetFullPath(absoluteRootPath.Trim());
    }

    public string NativeRootPath => _root;

    public string? SubjectId => _subjectId;

    public bool IsInitialized => _initialized;

    public Task<VoidResult<string>> InitializeAsync(
        string subjectId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);

        if (!string.Equals(subjectId, DysonWorkspaceSubjects.LocalFs, StringComparison.Ordinal))
        {
            return Task.FromResult(VoidResult<string>.AsError(
                $"Local workspace FS only accepts subject '{DysonWorkspaceSubjects.LocalFs}' (got '{subjectId}')."));
        }

        if (_initialized)
        {
            if (string.Equals(_subjectId, subjectId, StringComparison.Ordinal))
                return Task.FromResult(VoidResult<string>.Success);

            return Task.FromResult(VoidResult<string>.AsError(
                $"Workspace FS already initialized with subject '{_subjectId}'."));
        }

        _subjectId = subjectId;
        _initialized = true;
        return Task.FromResult(VoidResult<string>.Success);
    }

    public Result<string, string> ResolvePath(string path)
    {
        var ready = EnsureInitialized();
        if (ready.IsError)
            return Result<string, string>.AsError(ready.Error);

        return ResolveUnderWorkRoot(path);
    }

    public Result<string, string> GetRelativePath(string path)
    {
        var resolved = ResolvePath(path);
        if (resolved.IsError)
            return resolved;

        try
        {
            var rootTrimmed = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullTrimmed = resolved.Value.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullTrimmed, rootTrimmed, PathComparison))
                return Result<string, string>.AsValue("");

            var rel = Path.GetRelativePath(_root, resolved.Value);
            return Result<string, string>.AsValue(rel.Replace('\\', '/'));
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Invalid path: {ex.Message}");
        }
    }

    public Result<bool, string> FileExists(string path)
    {
        var resolved = ResolvePath(path);
        if (resolved.IsError)
            return Result<bool, string>.AsError(resolved.Error);

        try
        {
            return Result<bool, string>.AsValue(File.Exists(resolved.Value));
        }
        catch (Exception ex)
        {
            return Result<bool, string>.AsError($"Failed to check file: {ex.Message}");
        }
    }

    public Result<bool, string> DirectoryExists(string path)
    {
        var resolved = ResolvePath(path);
        if (resolved.IsError)
            return Result<bool, string>.AsError(resolved.Error);

        try
        {
            return Result<bool, string>.AsValue(Directory.Exists(resolved.Value));
        }
        catch (Exception ex)
        {
            return Result<bool, string>.AsError($"Failed to check directory: {ex.Message}");
        }
    }

    public Result<long, string> GetFileLength(string path)
    {
        var resolved = ResolvePath(path);
        if (resolved.IsError)
            return Result<long, string>.AsError(resolved.Error);

        try
        {
            if (!File.Exists(resolved.Value))
                return Result<long, string>.AsError($"File not found: {path}");

            return Result<long, string>.AsValue(new FileInfo(resolved.Value).Length);
        }
        catch (Exception ex)
        {
            return Result<long, string>.AsError($"Failed to get file length: {ex.Message}");
        }
    }

    public Result<string, string> ReadAllText(string path)
    {
        var resolved = ResolvePath(path);
        if (resolved.IsError)
            return resolved;

        try
        {
            if (!File.Exists(resolved.Value))
                return Result<string, string>.AsError($"File not found: {path}");

            return Result<string, string>.AsValue(File.ReadAllText(resolved.Value));
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Failed to read file: {ex.Message}");
        }
    }

    public Result<byte[], string> ReadAllBytes(string path)
    {
        var resolved = ResolvePath(path);
        if (resolved.IsError)
            return Result<byte[], string>.AsError(resolved.Error);

        try
        {
            if (!File.Exists(resolved.Value))
                return Result<byte[], string>.AsError($"File not found: {path}");

            return Result<byte[], string>.AsValue(File.ReadAllBytes(resolved.Value));
        }
        catch (Exception ex)
        {
            return Result<byte[], string>.AsError($"Failed to read file: {ex.Message}");
        }
    }

    public Result<byte[], string> ReadFileHead(string path, int maxBytes)
    {
        if (maxBytes < 0)
            return Result<byte[], string>.AsError("maxBytes must be non-negative.");

        var resolved = ResolvePath(path);
        if (resolved.IsError)
            return Result<byte[], string>.AsError(resolved.Error);

        try
        {
            if (!File.Exists(resolved.Value))
                return Result<byte[], string>.AsError($"File not found: {path}");

            using var stream = File.OpenRead(resolved.Value);
            var buf = new byte[maxBytes];
            var read = stream.Read(buf, 0, buf.Length);
            if (read == buf.Length)
                return Result<byte[], string>.AsValue(buf);

            var sliced = new byte[read];
            Buffer.BlockCopy(buf, 0, sliced, 0, read);
            return Result<byte[], string>.AsValue(sliced);
        }
        catch (Exception ex)
        {
            return Result<byte[], string>.AsError($"Failed to read file: {ex.Message}");
        }
    }

    public Result<DysonWorkspaceLineSlice, string> ReadLineSlice(
        string path,
        int startLine,
        int? maxLines,
        int maxChars,
        int maxLineChars)
    {
        if (maxLineChars < 1)
            return Result<DysonWorkspaceLineSlice, string>.AsError("maxLineChars must be at least 1.");
        if (maxChars < 1)
            return Result<DysonWorkspaceLineSlice, string>.AsError("maxChars must be at least 1.");
        if (maxLines is < 1)
            return Result<DysonWorkspaceLineSlice, string>.AsError("maxLines must be at least 1 when set.");

        var resolved = ResolvePath(path);
        if (resolved.IsError)
            return Result<DysonWorkspaceLineSlice, string>.AsError(resolved.Error);

        try
        {
            if (!File.Exists(resolved.Value))
                return Result<DysonWorkspaceLineSlice, string>.AsError($"File not found: {path}");

            using var stream = new FileStream(
                resolved.Value,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var fileLength = stream.Length;
            var tailed = startLine < 0;
            var lines = new List<DysonWorkspaceLine>();
            var rawChars = 0;
            var truncated = false;
            var lineNumber = 0;

            if (tailed)
            {
                var windowSize = startLine <= -int.MaxValue ? int.MaxValue : -startLine;
                var window = new Queue<DysonWorkspaceLine>();
                while (TryReadBoundedLine(reader, maxLineChars, capture: true, out var text, out var clipped))
                {
                    lineNumber++;
                    if (window.Count == windowSize)
                        window.Dequeue();
                    window.Enqueue(new DysonWorkspaceLine(lineNumber, text, clipped));
                }

                foreach (var line in window)
                {
                    if (maxLines is int ml && lines.Count >= ml)
                    {
                        truncated = true;
                        break;
                    }

                    if (rawChars + line.Text.Length > maxChars)
                    {
                        if (lines.Count == 0)
                            lines.Add(line);
                        truncated = true;
                        break;
                    }

                    lines.Add(line);
                    rawChars += line.Text.Length;
                }
            }
            else
            {
                var first = startLine == 0 ? 1 : startLine;
                while (TryReadBoundedLine(
                    reader,
                    maxLineChars,
                    capture: lineNumber + 1 >= first,
                    out var text,
                    out var clipped))
                {
                    lineNumber++;
                    if (lineNumber < first)
                        continue;

                    if (rawChars + text.Length > maxChars)
                    {
                        if (lines.Count == 0)
                            lines.Add(new DysonWorkspaceLine(lineNumber, text, clipped));
                        truncated = true;
                        break;
                    }

                    lines.Add(new DysonWorkspaceLine(lineNumber, text, clipped));
                    rawChars += text.Length;

                    if (maxLines is int ml && lines.Count >= ml)
                    {
                        truncated = reader.Peek() >= 0;
                        break;
                    }
                }
            }

            var start = lines.Count == 0 ? 0 : lines[0].LineNumber;
            var next = lines.Count == 0 ? lineNumber + 1 : lines[^1].LineNumber + 1;
            return Result<DysonWorkspaceLineSlice, string>.AsValue(
                new DysonWorkspaceLineSlice(lines, start, next, truncated, fileLength, tailed));
        }
        catch (Exception ex)
        {
            return Result<DysonWorkspaceLineSlice, string>.AsError($"Failed to read file: {ex.Message}");
        }
    }

    public VoidResult<string> WriteAllText(string path, string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var resolved = ResolvePath(path);
        if (resolved.IsError)
            return VoidResult<string>.AsError(resolved.Error);

        try
        {
            var dir = Path.GetDirectoryName(resolved.Value);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(resolved.Value, contents);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Failed to write file: {ex.Message}", ex);
        }
    }

    public VoidResult<string> WriteAllBytes(string path, byte[] contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var resolved = ResolvePath(path);
        if (resolved.IsError)
            return VoidResult<string>.AsError(resolved.Error);

        try
        {
            var dir = Path.GetDirectoryName(resolved.Value);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllBytes(resolved.Value, contents);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Failed to write file: {ex.Message}", ex);
        }
    }

    public VoidResult<string> CreateDirectory(string path)
    {
        var resolved = ResolvePath(path);
        if (resolved.IsError)
            return VoidResult<string>.AsError(resolved.Error);

        try
        {
            Directory.CreateDirectory(resolved.Value);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Failed to create directory: {ex.Message}", ex);
        }
    }

    public Result<IReadOnlyList<DysonWorkspaceEntry>, string> EnumerateEntries(string path)
    {
        var resolved = ResolvePath(path);
        if (resolved.IsError)
            return Result<IReadOnlyList<DysonWorkspaceEntry>, string>.AsError(resolved.Error);

        try
        {
            if (!Directory.Exists(resolved.Value))
                return Result<IReadOnlyList<DysonWorkspaceEntry>, string>.AsError(
                    $"Directory not found: {path}");

            var list = new List<DysonWorkspaceEntry>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(resolved.Value))
            {
                var name = Path.GetFileName(entry);
                if (string.IsNullOrEmpty(name))
                    continue;

                list.Add(new DysonWorkspaceEntry(name, Directory.Exists(entry)));
            }

            return Result<IReadOnlyList<DysonWorkspaceEntry>, string>.AsValue(list);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<DysonWorkspaceEntry>, string>.AsError(
                $"Failed to list directory: {ex.Message}");
        }
    }

    public Result<IReadOnlyList<string>, string> EnumerateFiles(
        string directoryPath,
        string searchPattern = "*",
        bool recursive = false)
    {
        var resolved = ResolvePath(directoryPath);
        if (resolved.IsError)
            return Result<IReadOnlyList<string>, string>.AsError(resolved.Error);

        var pattern = string.IsNullOrWhiteSpace(searchPattern) ? "*" : searchPattern;
        try
        {
            if (!Directory.Exists(resolved.Value))
                return Result<IReadOnlyList<string>, string>.AsError(
                    $"Directory not found: {directoryPath}");

            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.EnumerateFiles(resolved.Value, pattern, option).ToList();
            return Result<IReadOnlyList<string>, string>.AsValue(files);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<string>, string>.AsError(
                $"Failed to enumerate files: {ex.Message}");
        }
    }

    public VoidResult<string> DeleteFile(string path)
    {
        var resolved = ResolvePath(path);
        if (resolved.IsError)
            return VoidResult<string>.AsError(resolved.Error);

        try
        {
            if (!File.Exists(resolved.Value))
                return VoidResult<string>.AsError($"File not found: {path}");

            File.Delete(resolved.Value);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Failed to delete file: {ex.Message}", ex);
        }
    }

    public VoidResult<string> DeleteDirectory(string path, bool recursive = false)
    {
        var resolved = ResolvePath(path);
        if (resolved.IsError)
            return VoidResult<string>.AsError(resolved.Error);

        try
        {
            if (!Directory.Exists(resolved.Value))
                return VoidResult<string>.AsError($"Directory not found: {path}");

            Directory.Delete(resolved.Value, recursive);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Failed to delete directory: {ex.Message}", ex);
        }
    }

    public VoidResult<string> Move(string sourceRelativePath, string destinationRelativePath)
    {
        var sourceResolved = ResolvePath(sourceRelativePath);
        if (sourceResolved.IsError)
            return VoidResult<string>.AsError(sourceResolved.Error);

        var destResolved = ResolvePath(destinationRelativePath);
        if (destResolved.IsError)
            return VoidResult<string>.AsError(destResolved.Error);

        var source = sourceResolved.Value;
        var dest = destResolved.Value;

        if (string.Equals(source, dest, PathComparison))
            return VoidResult<string>.AsError("Source and destination are the same.");

        var isDirectory = Directory.Exists(source);
        var isFile = File.Exists(source);
        if (!isDirectory && !isFile)
            return VoidResult<string>.AsError($"Path not found: {sourceRelativePath}");

        if (isDirectory)
        {
            var sourcePrefix = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                               + Path.DirectorySeparatorChar;
            if (dest.StartsWith(sourcePrefix, PathComparison))
                return VoidResult<string>.AsError("Cannot move a directory into itself.");
        }

        var destName = Path.GetFileName(dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(destName)
            || destName is "." or ".."
            || destName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return VoidResult<string>.AsError("Invalid destination name.");
        }

        if (Directory.Exists(dest) || File.Exists(dest))
            return VoidResult<string>.AsError($"Destination already exists: {destinationRelativePath}");

        var destParent = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destParent) && !Directory.Exists(destParent))
            return VoidResult<string>.AsError($"Destination parent not found: {destinationRelativePath}");

        try
        {
            if (isDirectory)
                Directory.Move(source, dest);
            else
                File.Move(source, dest);

            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Failed to move: {ex.Message}", ex);
        }
    }

    public Result<IDysonWorkspaceChangeWatcher, string> CreateWatcher()
    {
        var ready = EnsureInitialized();
        if (ready.IsError)
            return Result<IDysonWorkspaceChangeWatcher, string>.AsError(ready.Error);

        return Result<IDysonWorkspaceChangeWatcher, string>.AsValue(
            new DysonLocalWorkspaceChangeWatcher(_root));
    }

    private VoidResult<string> EnsureInitialized()
    {
        if (_initialized)
            return VoidResult<string>.Success;

        return VoidResult<string>.AsError(
            "Workspace filesystem is not initialized. Call InitializeAsync first.");
    }

    private Result<string, string> ResolveUnderWorkRoot(string path)
    {
        try
        {
            var combined = string.IsNullOrWhiteSpace(path) || path is "." or "./"
                ? _root
                : Path.IsPathRooted(path)
                    ? Path.GetFullPath(path)
                    : Path.GetFullPath(Path.Combine(
                        _root,
                        path.Replace('/', Path.DirectorySeparatorChar)));

            if (!IsUnderWorkRoot(combined))
                return Result<string, string>.AsError($"Path escapes work directory: {path}");

            return Result<string, string>.AsValue(combined);
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Invalid path: {ex.Message}");
        }
    }

    private bool IsUnderWorkRoot(string fullPath)
    {
        var root = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(fullPath);
        var rootTrimmed = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullTrimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(fullTrimmed, rootTrimmed, PathComparison))
            return true;

        return full.StartsWith(root, PathComparison);
    }

    /// <returns>false when EOF is hit at the start of a line (no line to emit).</returns>
    private static bool TryReadBoundedLine(
        StreamReader reader,
        int maxLineChars,
        bool capture,
        out string text,
        out bool clipped)
    {
        text = "";
        clipped = false;
        StringBuilder? sb = capture ? new StringBuilder() : null;
        var count = 0;

        while (count < maxLineChars)
        {
            var c = reader.Read();
            if (c < 0)
            {
                if (count == 0)
                    return false;
                if (sb is not null)
                    text = ToLineText(sb);
                return true;
            }

            if (c == '\n')
            {
                if (sb is not null)
                    text = ToLineText(sb);
                return true;
            }

            sb?.Append((char)c);
            count++;
        }

        var next = reader.Peek();
        if (next == '\n')
        {
            reader.Read();
        }
        else if (next >= 0)
        {
            clipped = true;
            while (true)
            {
                var c = reader.Read();
                if (c < 0 || c == '\n')
                    break;
            }
        }

        if (sb is not null)
            text = ToLineText(sb);
        return true;
    }

    private static string ToLineText(StringBuilder sb)
    {
        var n = sb.Length;
        if (n > 0 && sb[n - 1] == '\r')
            n--;
        return sb.ToString(0, n);
    }
}
