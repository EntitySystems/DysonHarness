using System.Globalization;
using DysonHarness;
using Microsoft.JSInterop;

namespace Harness.UI.Theme;

public sealed class ThemeService(IJSRuntime js)
{
    public const string DefaultTheme = "dark";
    public const string DefaultAccent = "blue";
    public const string DefaultAccentHex = "#4c8bf5";

    private readonly IJSRuntime _js = js ?? throw new ArgumentNullException(nameof(js));
    private bool _initialized;

    public string Theme { get; private set; } = DefaultTheme;
    public string Accent { get; private set; } = DefaultAccent;

    public event Action? Changed;

    public static IReadOnlyList<string> Themes { get; } = ["dark", "light"];
    public static IReadOnlyList<string> Accents { get; } = ["blue", "green", "red", "purple"];

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        try
        {
            var stored = await _js.InvokeAsync<ThemePreference?>("dysonTheme.get", cancellationToken)
                .ConfigureAwait(false);
            if (stored is not null)
            {
                if (IsValidTheme(stored.Theme))
                    Theme = stored.Theme;
                if (IsValidAccent(stored.Accent))
                    Accent = stored.Accent;
            }

            await ApplyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (JSException)
        {
            // Prerender / JS not ready — keep defaults until interactive.
        }
        catch (InvalidOperationException)
        {
            // JS interop unavailable during static render.
        }

        _initialized = true;
    }

    public async Task SetThemeAsync(string theme, CancellationToken cancellationToken = default)
    {
        if (!IsValidTheme(theme) || Theme == theme)
            return;

        Theme = theme;
        await PersistAndApplyAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public async Task SetAccentAsync(string accent, CancellationToken cancellationToken = default)
    {
        if (!IsValidAccent(accent) || Accent == accent)
            return;

        Accent = accent;
        await PersistAndApplyAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>
    /// Captures the applied document theme for a newly constructed root session.
    /// The computed accent variable, rather than the selected accent name, is the source of truth.
    /// </summary>
    public async Task<DysonUiThemeSnapshot> CaptureSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var resolved = await _js.InvokeAsync<ThemeResolvedSnapshot?>(
                    "dysonTheme.getResolved", cancellationToken)
                .ConfigureAwait(false);
            if (resolved is null
                || !TryNormalizeTheme(resolved.Theme, out var theme)
                || !TryNormalizeAccentHex(resolved.AccentHex, out var accentHex))
            {
                return DysonUiThemeSnapshot.Default;
            }

            return new DysonUiThemeSnapshot(theme, accentHex);
        }
        catch (JSException)
        {
            return DysonUiThemeSnapshot.Default;
        }
        catch (InvalidOperationException)
        {
            return DysonUiThemeSnapshot.Default;
        }
    }

    private async Task PersistAndApplyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _js.InvokeVoidAsync("dysonTheme.set", cancellationToken, Theme, Accent)
                .ConfigureAwait(false);
        }
        catch (JSException)
        {
            await ApplyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Ignore when JS is unavailable.
        }
    }

    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _js.InvokeVoidAsync("dysonTheme.apply", cancellationToken, Theme, Accent)
                .ConfigureAwait(false);
        }
        catch (JSException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool IsValidTheme(string? theme) =>
        theme is not null && Themes.Contains(theme, StringComparer.OrdinalIgnoreCase);

    private static bool IsValidAccent(string? accent) =>
        accent is not null && Accents.Contains(accent, StringComparer.OrdinalIgnoreCase);

    private static bool TryNormalizeTheme(string? value, out string theme)
    {
        theme = DefaultTheme;
        if (string.Equals(value, "light", StringComparison.OrdinalIgnoreCase))
        {
            theme = "light";
            return true;
        }

        return string.Equals(value, DefaultTheme, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeAccentHex(string? accent, out string accentHex)
    {
        accentHex = DefaultAccentHex;
        if (string.IsNullOrWhiteSpace(accent))
            return false;

        var value = accent.Trim();
        if (value.Length == 4 && value[0] == '#'
            && Uri.IsHexDigit(value[1]) && Uri.IsHexDigit(value[2]) && Uri.IsHexDigit(value[3]))
        {
            accentHex = $"#{char.ToLowerInvariant(value[1])}{char.ToLowerInvariant(value[1])}" +
                        $"{char.ToLowerInvariant(value[2])}{char.ToLowerInvariant(value[2])}" +
                        $"{char.ToLowerInvariant(value[3])}{char.ToLowerInvariant(value[3])}";
            return true;
        }

        if (value.Length == 7 && value[0] == '#'
            && Uri.IsHexDigit(value[1]) && Uri.IsHexDigit(value[2]) && Uri.IsHexDigit(value[3])
            && Uri.IsHexDigit(value[4]) && Uri.IsHexDigit(value[5]) && Uri.IsHexDigit(value[6]))
        {
            accentHex = value.ToLowerInvariant();
            return true;
        }

        if (value.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) && value.EndsWith(')'))
        {
            var components = value[4..^1].Split(',', StringSplitOptions.TrimEntries);
            if (components.Length == 3
                && int.TryParse(components[0], NumberStyles.None, CultureInfo.InvariantCulture, out var red)
                && red is >= 0 and <= 255
                && int.TryParse(components[1], NumberStyles.None, CultureInfo.InvariantCulture, out var green)
                && green is >= 0 and <= 255
                && int.TryParse(components[2], NumberStyles.None, CultureInfo.InvariantCulture, out var blue)
                && blue is >= 0 and <= 255)
            {
                accentHex = $"#{red:x2}{green:x2}{blue:x2}";
                return true;
            }
        }

        return false;
    }

    private sealed class ThemePreference
    {
        public string Theme { get; set; } = DefaultTheme;
        public string Accent { get; set; } = DefaultAccent;
    }

    private sealed class ThemeResolvedSnapshot
    {
        public string? Theme { get; set; }
        public string? AccentHex { get; set; }
    }
}
