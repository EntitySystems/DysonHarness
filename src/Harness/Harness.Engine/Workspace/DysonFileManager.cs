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

    private readonly IDysonWorkspaceFileSystem _fs;

    public DysonFileManager(IDysonWorkspaceFileSystem workspaceFileSystem)
    {
        _fs = workspaceFileSystem ?? throw new ArgumentNullException(nameof(workspaceFileSystem));
    }

    public string WorkRoot => _fs.NativeRootPath;

    /// <summary>Ensures <c>{workRoot}/.dyson/plans/</c> exists.</summary>
    public async Task<VoidResult<string>> EnsurePlansDirectoryAsync(
        CancellationToken cancellationToken = default)
    {
        var created = await _fs.CreateDirectoryAsync(PlansRelativeDir, cancellationToken)
            .ConfigureAwait(false);
        if (created.IsError)
            return VoidResult<string>.AsError($"Failed to create plans directory: {created.Error}");

        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Writes a new plan markdown file as <c>.dyson/plans/{slug}-{hash}.md</c>
    /// and returns the workspace-relative path (forward slashes).
    /// </summary>
    public async Task<Result<string, string>> WriteNewPlanAsync(
        string titleSlug,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var ensured = await EnsurePlansDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (ensured.IsError)
            return Result<string, string>.AsError(ensured.Error);

        var slug = SanitizeSlug(titleSlug);
        var hash = ShortContentHash(markdown);
        var fileName = $"{slug}-{hash}.md";
        var relative = $"{PlansRelativeDir}/{fileName}";

        var resolved = _fs.ResolvePath(relative);
        if (resolved.IsError)
            return resolved;

        if (!IsUnderPlansDir(resolved.Value))
            return Result<string, string>.AsError($"Plan path escapes {PlansRelativeDir}: {relative}");

        var written = await _fs.WriteAllTextAsync(relative, markdown, cancellationToken)
            .ConfigureAwait(false);
        if (written.IsError)
            return Result<string, string>.AsError($"Failed to write plan: {written.Error}");

        return Result<string, string>.AsValue(relative);
    }

    /// <summary>Reads UTF-8 text at a workspace-relative (or absolute-under-root) path.</summary>
    public async Task<Result<string, string>> ReadTextAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var read = await _fs.ReadAllTextAsync(relativePath, cancellationToken).ConfigureAwait(false);
        if (read.IsError)
            return Result<string, string>.AsError(read.Error);

        return read;
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

    private bool IsUnderPlansDir(string fullPath)
    {
        var plansResolved = _fs.ResolvePath(PlansRelativeDir);
        if (plansResolved.IsError)
            return false;

        var plansRoot = plansResolved.Value;
        var root = plansRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(fullPath);
        return full.StartsWith(
            root,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
