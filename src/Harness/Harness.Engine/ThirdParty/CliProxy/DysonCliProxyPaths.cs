namespace DysonHarness;

/// <summary>Program-root layout for the pinned CLIProxyAPI binary and local config.</summary>
public static class DysonCliProxyPaths
{
    public static string InstallRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "external", "cliproxy"));

    public static string VersionDirectory(string version) =>
        Path.GetFullPath(Path.Combine(InstallRoot, version));

    public static string ConfigPath => Path.Combine(InstallRoot, "config.yaml");

    public static string AuthsDirectory => Path.Combine(InstallRoot, "auths");

    public static string ExecutableFileName =>
        OperatingSystem.IsWindows() ? "cli-proxy-api.exe" : "cli-proxy-api";

    public static string ExpectedExecutablePath(string version) =>
        Path.Combine(VersionDirectory(version), ExecutableFileName);
}
