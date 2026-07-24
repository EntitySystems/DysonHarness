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

    /// <summary>Creates the mode root directory if missing; returns the root path.</summary>
    public static string EnsureRoot(DysonAppMode mode)
    {
        var root = GetRoot(mode);
        Directory.CreateDirectory(root);
        return root;
    }
}
