namespace DysonHarness;

/// <summary>Program-root layout for the pinned CLIProxyAPI binary and local config.</summary>
public static class DysonCliProxyPaths
{
    public const string AuthsDirectoryName = "auths";
    public const string ConfigFileName = "config.yaml";
    public const string KeysFileName = "keys.json";

    public static string InstallRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "external", "cliproxy"));

    public static string VersionDirectory(string version) =>
        VersionDirectory(InstallRoot, version);

    public static string VersionDirectory(string installRoot, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return Path.GetFullPath(Path.Combine(installRoot, version));
    }

    public static string ConfigPath => Path.Combine(InstallRoot, ConfigFileName);

    public static string AuthsDirectory => Path.Combine(InstallRoot, AuthsDirectoryName);

    public static string ExecutableFileName =>
        OperatingSystem.IsWindows() ? "cli-proxy-api.exe" : "cli-proxy-api";

    public static string ExpectedExecutablePath(string version) =>
        ExpectedExecutablePath(InstallRoot, version);

    public static string ExpectedExecutablePath(string installRoot, string version) =>
        Path.Combine(VersionDirectory(installRoot, version), ExecutableFileName);

    /// <summary>
    /// Delete sibling version folders under <paramref name="installRoot"/> except
    /// <paramref name="keepVersion"/>. Skips <c>auths/</c> and files
    /// (<c>config.yaml</c>, <c>keys.json</c>). Call only on force reinstall.
    /// </summary>
    public static void PruneObsoleteVersionDirectories(string installRoot, string keepVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(keepVersion);

        if (!Directory.Exists(installRoot))
            return;

        var keepDir = VersionDirectory(installRoot, keepVersion);
        foreach (var dir in Directory.GetDirectories(installRoot))
        {
            var name = Path.GetFileName(dir.AsSpan());
            if (name.Equals(AuthsDirectoryName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!LooksLikeVersionFolderName(name))
                continue;
            if (Path.GetFullPath(dir).Equals(keepDir, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // ponytail: leftover version dirs are harmless if a lock blocks delete
            }
        }
    }

    internal static bool LooksLikeVersionFolderName(ReadOnlySpan<char> name)
    {
        // 7.2.145 — three numeric segments, nothing else.
        var dots = 0;
        var segmentLength = 0;
        foreach (var c in name)
        {
            if (c == '.')
            {
                if (segmentLength == 0)
                    return false;
                dots++;
                segmentLength = 0;
                continue;
            }

            if (!char.IsAsciiDigit(c))
                return false;
            segmentLength++;
        }

        return dots == 2 && segmentLength > 0;
    }
}
