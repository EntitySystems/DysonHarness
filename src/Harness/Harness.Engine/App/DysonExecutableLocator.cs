namespace DysonHarness;

/// <summary>Finds a CLI executable on PATH, then in extra install directories.</summary>
internal static class DysonExecutableLocator
{
    private const string DefaultWindowsPathExt = ".EXE;.CMD;.BAT";

    /// <summary>Full path of <paramref name="name"/>, or null.</summary>
    internal static string? Find(string name, IReadOnlyList<string> extraDirs) =>
        Find(
            name,
            extraDirs,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT"),
            File.Exists);

    /// <summary>Test seam: injected PATH, PATHEXT and existence check.</summary>
    /// <remarks>
    /// ponytail: duplicates DysonEmbeddedRuntimeInstaller.FindOnPath (PATH walk, no PATHEXT, no extra dirs).
    /// Upgrade is one shared finder if that installer needs PATHEXT or install dirs.
    /// </remarks>
    internal static string? Find(
        string name,
        IReadOnlyList<string> extraDirs,
        string? pathEnv,
        string? pathExt,
        Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var extensions = ExtensionsFor(name, pathExt);
        if (pathEnv is not null)
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var hit = Probe(dir.Trim(), name, extensions, fileExists);
                if (hit is not null)
                    return hit;
            }
        }

        foreach (var dir in extraDirs)
        {
            var expanded = Expand(dir);
            if (expanded.Length == 0)
                continue;

            var hit = Probe(expanded, name, extensions, fileExists);
            if (hit is not null)
                return hit;
        }

        return null;
    }

    private static string[] ExtensionsFor(string name, string? pathExt)
    {
        if (!OperatingSystem.IsWindows())
            return [];

        if (Path.HasExtension(name))
            return [""];

        var raw = string.IsNullOrWhiteSpace(pathExt) ? DefaultWindowsPathExt : pathExt;
        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? Probe(string dir, string name, string[] extensions, Func<string, bool> fileExists)
    {
        if (dir.Length == 0)
            return null;

        if (extensions.Length == 0)
        {
            var candidate = Path.Combine(dir, name);
            return fileExists(candidate) ? Path.GetFullPath(candidate) : null;
        }

        foreach (var ext in extensions)
        {
            var candidate = Path.Combine(dir, ext.Length == 0 ? name : name + ext);
            if (fileExists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static string Expand(string dir)
    {
        var trimmed = dir.Trim();
        if (trimmed.Length == 0)
            return "";

        if (trimmed[0] == '~')
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            trimmed = trimmed.Length == 1
                ? home
                : trimmed[1] is '/' or '\\'
                    ? Path.Combine(home, trimmed[2..])
                    : trimmed;
        }

        return Environment.ExpandEnvironmentVariables(trimmed).Trim();
    }
}
