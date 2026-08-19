namespace DysonHarness;

/// <summary>
/// Platform app-data roots scoped by <see cref="DysonAppMode"/> (DysonDev / DysonTest / DysonProd).
/// </summary>
public static class DysonAppPaths
{
    public static string GetModeFolderName(DysonAppMode mode) => mode switch
    {
        DysonAppMode.Prod => "DysonProd",
        DysonAppMode.Test => "DysonTest",
        _ => "DysonDev",
    };

    public static string GetBaseDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
            return xdg;

        var linuxHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(linuxHome, ".local", "share");
    }

    public static string GetRoot(DysonAppMode mode) =>
        Path.Combine(GetBaseDirectory(), GetModeFolderName(mode));

    public static string GetDatabasePath(DysonAppMode mode) =>
        Path.Combine(GetRoot(mode), "dyson.db");

    /// <summary>
    /// Host diagnostic log sibling of <c>dyson.db</c> (<c>{root}/dyson.log</c>). Not SQLite.
    /// </summary>
    public static string GetLogFilePath(DysonAppMode mode) =>
        Path.Combine(GetRoot(mode), "dyson.log");

    public static string GetPluginsDirectory(DysonAppMode mode) =>
        Path.Combine(GetRoot(mode), "plugins");

    public static string GetRuntimesDirectory(DysonAppMode mode) =>
        Path.Combine(GetRoot(mode), "runtimes");

    public static string GetPluginDataDirectory(DysonAppMode mode) =>
        Path.Combine(GetRoot(mode), "plugin-data");

    public static string GetPluginSecurityDirectory(DysonAppMode mode) =>
        Path.Combine(GetRoot(mode), "plugin-security");

    public static string GetPluginVariableProtectionKeyPath(DysonAppMode mode) =>
        Path.Combine(GetPluginSecurityDirectory(mode), "variable-protection.key");

    /// <summary>Creates the mode root directory if missing; returns the root path.</summary>
    public static string EnsureRoot(DysonAppMode mode)
    {
        var root = GetRoot(mode);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Creates the mode-scoped global plugin package root if missing.</summary>
    public static string EnsurePluginsDirectory(DysonAppMode mode)
    {
        var path = GetPluginsDirectory(mode);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Creates the mode-scoped embedded-runtimes root if missing. Call on first install, not at startup.</summary>
    public static string EnsureRuntimesDirectory(DysonAppMode mode)
    {
        var path = GetRuntimesDirectory(mode);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Creates the mode-scoped global persistent plugin-data root if missing.</summary>
    public static string EnsurePluginDataDirectory(DysonAppMode mode)
    {
        var path = GetPluginDataDirectory(mode);
        Directory.CreateDirectory(path);
        return path;
    }
}
