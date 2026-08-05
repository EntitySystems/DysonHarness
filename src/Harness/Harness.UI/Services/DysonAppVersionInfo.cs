using System.Reflection;
using System.Text.Json;

namespace Harness.UI.Services;

/// <summary>
/// Build-stamped <c>version.json</c> shipped next to the app executable
/// (written by <c>scripts/write-version-json.*</c>). Missing or unstamped
/// (<c>1.0.0</c>) builds disable the in-app updater.
/// </summary>
public sealed class DysonAppVersionInfo
{
    public const string FileName = "version.json";
    public const string DefaultRepo = "EntitySystems/DysonHarness";

    /// <summary>Continuous CalVer is <c>YYYY.M.run</c>; anything below this year is a local/dev build.</summary>
    private const int FirstCalVerYear = 2026;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static DysonAppVersionInfo? _local;

    public string Version { get; init; } = "";
    public string InformationalVersion { get; init; } = "";
    public string Rid { get; init; } = "";
    public string Repo { get; init; } = "";

    /// <summary>Local build info, read once from <see cref="AppContext.BaseDirectory"/>.</summary>
    public static DysonAppVersionInfo Local => _local ??= ReadFrom(AppContext.BaseDirectory) ?? FromAssembly();

    public System.Version? CalVer => ParseCalVer(Version);

    /// <summary>True when this build carries a real continuous CalVer, i.e. the updater may run.</summary>
    public bool IsStampedRelease => CalVer is { Major: >= FirstCalVerYear };

    public string EffectiveRepo => string.IsNullOrWhiteSpace(Repo) ? DefaultRepo : Repo.Trim();

    /// <summary>Reads <c>version.json</c> from <paramref name="directory"/>; null when missing or unreadable.</summary>
    public static DysonAppVersionInfo? ReadFrom(string directory)
    {
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path))
                return null;

            var info = JsonSerializer.Deserialize<DysonAppVersionInfo>(File.ReadAllText(path), JsonOptions);
            return string.IsNullOrWhiteSpace(info?.Version) ? null : info;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses a CalVer tag (<c>2026.8.142</c>, optional <c>v</c> prefix and <c>+sha</c> suffix).</summary>
    public static System.Version? ParseCalVer(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = raw.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        var cut = text.IndexOfAny(['+', '-']);
        if (cut >= 0)
            text = text[..cut];

        if (!System.Version.TryParse(text, out var version))
            return null;

        // Normalize so 2026.8 and 2026.8.0 compare equal (System.Version treats -1 < 0).
        return new System.Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    /// <summary>True when <paramref name="candidate"/> parses to a strictly higher CalVer than <paramref name="current"/>.</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        var remote = ParseCalVer(candidate);
        var local = ParseCalVer(current);
        return remote is not null && local is not null && remote > local;
    }

    private static DysonAppVersionInfo FromAssembly()
    {
        var informational = typeof(DysonAppVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
        var plus = informational.IndexOf('+');
        return new DysonAppVersionInfo
        {
            Version = plus >= 0 ? informational[..plus] : informational,
            InformationalVersion = informational,
            Rid = "",
            Repo = DefaultRepo,
        };
    }
}
