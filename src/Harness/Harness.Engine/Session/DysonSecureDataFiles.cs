using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace DysonHarness;

/// <summary>
/// Temp files written by WriteSecureDataToFile. Paths are tracked in memory per <c>(root session, key)</c>;
/// a process restart drops the list and <see cref="SweepStale"/> is the backstop.
/// </summary>
public static class DysonSecureDataFiles
{
    public const string Extension = ".dysonsecret";
    private const int MaxSanitizedKeyLength = 32;

    // ponytail: process-wide map, entries removed only by DeleteSecureData; upgrade = per-session cleanup on Meta delete.
    private static readonly ConcurrentDictionary<string, List<string>> Written = new(StringComparer.Ordinal);

    /// <summary><c>{sanitizedKey}-{8 hex}.{Guid:N}.dysonsecret</c>.</summary>
    public static string BuildFileName(string key)
    {
        var sanitized = new StringBuilder(Math.Min(key.Length, MaxSanitizedKeyLength));
        foreach (var c in key.ToLowerInvariant())
        {
            if (sanitized.Length == MaxSanitizedKeyLength)
                break;
            sanitized.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-' ? c : '_');
        }

        var disambiguator = Guid.NewGuid().ToString("N")[..8];
        return $"{sanitized}-{disambiguator}.{Guid.NewGuid():N}{Extension}";
    }

    /// <summary>
    /// Writes the UTF-8 bytes of <paramref name="secret"/> (no BOM, nothing added) and records the path.
    /// Error text names the path and the IO message, never the bytes.
    /// </summary>
    public static Result<string, string> Write(Guid rootSessionId, string key, string secret, Action<string>? warn = null)
    {
        var path = Path.Combine(Path.GetTempPath(), BuildFileName(key));
        try
        {
            File.WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(secret));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<string, string>.AsError($"WriteSecureDataToFile: could not write {path}: {ex.Message}");
        }

        if (OperatingSystem.IsWindows())
            TryRestrictToCurrentUser(path, warn);

        var list = Written.GetOrAdd(MapKey(rootSessionId, key), static _ => []);
        lock (list)
            list.Add(path);
        return Result<string, string>.AsValue(path);
    }

    /// <summary>Paths recorded for this key (test seam and delete).</summary>
    public static IReadOnlyList<string> TrackedPaths(Guid rootSessionId, string key)
    {
        if (!Written.TryGetValue(MapKey(rootSessionId, key), out var list))
            return [];
        lock (list)
            return list.ToList();
    }

    /// <summary>Deletes every recorded file for the key. A file already gone is success.</summary>
    public static void DeleteFor(Guid rootSessionId, string key, Action<string>? warn = null)
    {
        if (!Written.TryRemove(MapKey(rootSessionId, key), out var list))
            return;

        List<string> paths;
        lock (list)
            paths = list.ToList();
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warn?.Invoke($"DeleteSecureData: could not delete {path}: {ex.Message}");
            }
        }
    }

    /// <summary>Shutdown: best-effort delete of every tracked file. Rows stay.</summary>
    public static void DeleteAllTracked()
    {
        foreach (var mapKey in Written.Keys.ToArray())
        {
            if (!Written.TryRemove(mapKey, out var list))
                continue;
            List<string> paths;
            lock (list)
                paths = list.ToList();
            foreach (var path in paths)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left for the startup sweep.
                }
            }
        }
    }

    /// <summary>Startup backstop: deletes <c>*.dysonsecret</c> in the temp dir older than <paramref name="maxAge"/> (default 7 days).</summary>
    public static int SweepStale(TimeSpan? maxAge = null, string? directory = null)
    {
        var cutoff = DateTime.UtcNow - (maxAge ?? TimeSpan.FromDays(7));
        var deleted = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory ?? Path.GetTempPath(), "*" + Extension))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) >= cutoff)
                        continue;
                    File.Delete(path);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort; a file still open by a command is left for the next sweep.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp dir unreadable: nothing to sweep.
        }

        return deleted;
    }

    private static string MapKey(Guid rootSessionId, string key) => $"{rootSessionId:N}/{key}";

    [SupportedOSPlatform("windows")]
    private static void TryRestrictToCurrentUser(string path, Action<string>? warn)
    {
        // ponytail: best effort; the OS temp dir is already per-user on this desktop app. Not a security boundary.
        try
        {
            var user = WindowsIdentity.GetCurrent().User;
            if (user is null)
                return;
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            warn?.Invoke($"WriteSecureDataToFile: could not restrict ACL on {path}: {ex.Message}");
        }
    }
}
