using System.Security.Cryptography;
using System.Text;

namespace DysonHarness;

/// <summary>
/// Workspace-scoped file helpers (plans under <c>.dyson/plans/</c>).
/// Paths must stay under the work root (same sandbox rules as MCP file tools).
/// </summary>
public sealed class DysonFileManager
{
    public const string PlansRelativeDir = ".dyson/plans";

    private readonly string _workRoot;

    public DysonFileManager(string workDirectoryAbsolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectoryAbsolutePath);
        _workRoot = Path.GetFullPath(workDirectoryAbsolutePath);
    }

    public string WorkRoot => _workRoot;

    /// <summary>Ensures <c>{workRoot}/.dyson/plans/</c> exists.</summary>
    public VoidResult<string> EnsurePlansDirectory()
    {
        var plansAbs = Path.Combine(_workRoot, ".dyson", "plans");
        try
        {
            Directory.CreateDirectory(plansAbs);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to create plans directory: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes a new plan markdown file as <c>.dyson/plans/{slug}-{hash}.md</c>
    /// and returns the workspace-relative path (forward slashes).
    /// </summary>
    public Result<string, string> WriteNewPlan(string titleSlug, string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var ensured = EnsurePlansDirectory();
        if (ensured.IsError)
            return Result<string, string>.AsError(ensured.Error);

        var slug = SanitizeSlug(titleSlug);
        var hash = ShortContentHash(markdown);
        var fileName = $"{slug}-{hash}.md";
        var relative = $"{PlansRelativeDir}/{fileName}";

        var resolved = ResolveUnderWorkRoot(relative);
        if (resolved.IsError)
            return resolved;

        if (!IsUnderPlansDir(resolved.Value))
            return Result<string, string>.AsError($"Plan path escapes {PlansRelativeDir}: {relative}");

        try
        {
            File.WriteAllText(resolved.Value, markdown, Encoding.UTF8);
            return Result<string, string>.AsValue(relative);
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Failed to write plan: {ex.Message}");
        }
    }

    /// <summary>Reads UTF-8 text at a workspace-relative (or absolute-under-root) path.</summary>
    public Result<string, string> ReadText(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var resolved = ResolveUnderWorkRoot(relativePath);
        if (resolved.IsError)
            return resolved;

        if (!File.Exists(resolved.Value))
            return Result<string, string>.AsError($"File not found: {relativePath}");

        try
        {
            return Result<string, string>.AsValue(File.ReadAllText(resolved.Value, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Failed to read file: {ex.Message}");
        }
    }

    public static string SanitizeSlug(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "plan";

        var lower = title.Trim().ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        var lastDash = false;
        foreach (var ch in lower)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (ch is ' ' or '-' or '_' or '.')
            {
                if (sb.Length == 0 || lastDash)
                    continue;
                sb.Append('-');
                lastDash = true;
            }
        }

        while (sb.Length > 0 && sb[^1] == '-')
            sb.Length--;

        return sb.Length == 0 ? "plan" : sb.ToString();
    }

    /// <summary>8–12 hex chars from SHA1(content + utc ticks).</summary>
    public static string ShortContentHash(string markdown, DateTime? utcNow = null)
    {
        var ticks = (utcNow ?? DateTime.UtcNow).Ticks;
        var bytes = Encoding.UTF8.GetBytes(markdown + ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var hash = SHA1.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant()[..10];
    }

    private Result<string, string> ResolveUnderWorkRoot(string path)
    {
        try
        {
            var combined = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(_workRoot, path.Replace('/', Path.DirectorySeparatorChar)));

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
        var root = _workRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(fullPath);
        if (string.Equals(
                full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                _workRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return true;
        }

        return full.StartsWith(
            root,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private bool IsUnderPlansDir(string fullPath)
    {
        var plansRoot = Path.GetFullPath(Path.Combine(_workRoot, ".dyson", "plans"));
        var root = plansRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(fullPath);
        return full.StartsWith(
            root,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
