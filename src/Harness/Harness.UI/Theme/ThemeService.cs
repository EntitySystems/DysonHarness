using Microsoft.JSInterop;

namespace Harness.UI.Theme;

public sealed class ThemeService(IJSRuntime js)
{
    public const string DefaultTheme = "dark";
    public const string DefaultAccent = "blue";

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

    private sealed class ThemePreference
    {
        public string Theme { get; set; } = DefaultTheme;
        public string Accent { get; set; } = DefaultAccent;
    }
}
