namespace DysonHarness;

/// <summary>
/// Immutable presentation value object: validated <c>light</c>/<c>dark</c> plus lowercase <c>#rrggbb</c>.
/// </summary>
public sealed class DysonUiThemeSnapshot
{
    public const string DefaultTheme = "dark";
    public const string DefaultAccentHex = "#4c8bf5";

    public static DysonUiThemeSnapshot Default { get; } = new(DefaultTheme, DefaultAccentHex);

    public string Theme { get; }
    public string AccentHex { get; }

    public DysonUiThemeSnapshot(string theme, string accentHex)
    {
        var validated = TryCreate(theme, accentHex);
        if (validated.IsError)
            throw new ArgumentException(validated.Error, nameof(theme));

        Theme = validated.Value.Theme;
        AccentHex = validated.Value.AccentHex;
    }

    private DysonUiThemeSnapshot(string theme, string accentHex, bool _)
    {
        Theme = theme;
        AccentHex = accentHex;
    }

    /// <summary>Creates a normalized snapshot or reports invalid presentation data.</summary>
    public static Result<DysonUiThemeSnapshot, string> TryCreate(string? theme, string? accentHex)
    {
        var normalizedTheme = theme?.Trim().ToLowerInvariant();
        if (normalizedTheme is not ("light" or "dark"))
            return Result<DysonUiThemeSnapshot, string>.AsError("Theme must be 'light' or 'dark'.");

        var normalizedAccent = NormalizeAccentHex(accentHex);
        if (normalizedAccent is null)
            return Result<DysonUiThemeSnapshot, string>.AsError("AccentHex must be a six-digit hex color.");

        return Result<DysonUiThemeSnapshot, string>.AsValue(
            new DysonUiThemeSnapshot(normalizedTheme, normalizedAccent, true));
    }

    /// <summary>Returns the default snapshot when UI-provided values are unavailable or invalid.</summary>
    public static DysonUiThemeSnapshot FromOrDefault(string? theme, string? accentHex)
    {
        var snapshot = TryCreate(theme, accentHex);
        return snapshot.IsSuccess ? snapshot.Value : Default;
    }

    private static string? NormalizeAccentHex(string? accentHex)
    {
        var value = accentHex?.Trim();
        if (value is null || value.Length != 7 || value[0] != '#')
            return null;

        for (var i = 1; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
                return null;
        }

        return value.ToLowerInvariant();
    }
}
