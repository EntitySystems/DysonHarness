using System.Text.Json;
using DysonHarness;
using Harness.UI.Theme;
using Microsoft.JSInterop;

namespace Harness.Tests;

public class ThemeServiceTests
{
    [Theory]
    [InlineData("light", "#ABC", "light", "#aabbcc")]
    [InlineData("DARK", "rgb(76, 139, 245)", "dark", "#4c8bf5")]
    [InlineData("dark", "rgb(61,191,122)", "dark", "#3dbf7a")]
    public async Task CaptureSnapshotAsync_normalizes_resolved_document_values(
        string theme,
        string accent,
        string expectedTheme,
        string expectedAccent)
    {
        var service = new ThemeService(new ThemeJsRuntime(theme, accent));

        var snapshot = await service.CaptureSnapshotAsync();

        Assert.Equal(expectedTheme, snapshot.Theme);
        Assert.Equal(expectedAccent, snapshot.AccentHex);
    }

    [Theory]
    [InlineData("system", "hsl(1 2% 3%)")]
    [InlineData("light", "rgb(256, 0, 0)")]
    public async Task CaptureSnapshotAsync_uses_default_for_invalid_resolved_document_values(
        string theme,
        string accent)
    {
        var service = new ThemeService(new ThemeJsRuntime(theme, accent));

        var snapshot = await service.CaptureSnapshotAsync();

        Assert.Equal(DysonUiThemeSnapshot.Default.Theme, snapshot.Theme);
        Assert.Equal(DysonUiThemeSnapshot.Default.AccentHex, snapshot.AccentHex);
    }

    [Fact]
    public async Task CaptureSnapshotAsync_uses_default_when_js_is_unavailable()
    {
        var service = new ThemeService(new UnavailableJsRuntime());

        var snapshot = await service.CaptureSnapshotAsync();

        Assert.Equal(DysonUiThemeSnapshot.Default.Theme, snapshot.Theme);
        Assert.Equal(DysonUiThemeSnapshot.Default.AccentHex, snapshot.AccentHex);
    }

    [Fact]
    public async Task InitializeAsync_reads_theme_and_accent_from_settings()
    {
        var settings = new MemorySettings();
        settings.Values[DysonAppSettingKeys.UiTheme] = "light";
        settings.Values[DysonAppSettingKeys.UiAccent] = "purple";
        var service = new ThemeService(new ThemeJsRuntime("dark", "#4c8bf5"), settings);

        await service.InitializeAsync();

        Assert.Equal("light", service.Theme);
        Assert.Equal("purple", service.Accent);
    }

    [Fact]
    public async Task SetTheme_and_SetAccent_write_settings_keys()
    {
        var settings = new MemorySettings();
        var service = new ThemeService(new ThemeJsRuntime("dark", "#4c8bf5"), settings);

        await service.SetThemeAsync("light");
        await service.SetAccentAsync("green");

        Assert.Equal("light", settings.Values[DysonAppSettingKeys.UiTheme]);
        Assert.Equal("green", settings.Values[DysonAppSettingKeys.UiAccent]);
    }

    [Fact]
    public async Task InitializeAsync_ignores_invalid_stored_settings()
    {
        var settings = new MemorySettings();
        settings.Values[DysonAppSettingKeys.UiTheme] = "neon";
        settings.Values[DysonAppSettingKeys.UiAccent] = "hotpink";
        var service = new ThemeService(new ThemeJsRuntime("dark", "#4c8bf5"), settings);

        await service.InitializeAsync();

        Assert.Equal(ThemeService.DefaultTheme, service.Theme);
        Assert.Equal(ThemeService.DefaultAccent, service.Accent);
    }

    [Fact]
    public async Task InitializeAsync_prefers_settings_over_localStorage()
    {
        var settings = new MemorySettings();
        settings.Values[DysonAppSettingKeys.UiTheme] = "LIGHT";
        settings.Values[DysonAppSettingKeys.UiAccent] = "Purple";
        var service = new ThemeService(
            new ThemeJsRuntime("dark", "#4c8bf5", storedTheme: "dark", storedAccent: "red"),
            settings);

        await service.InitializeAsync();

        Assert.Equal("light", service.Theme);
        Assert.Equal("purple", service.Accent);
        Assert.Equal("LIGHT", settings.Values[DysonAppSettingKeys.UiTheme]);
        Assert.Equal("Purple", settings.Values[DysonAppSettingKeys.UiAccent]);
    }

    [Fact]
    public async Task InitializeAsync_migrates_localStorage_when_settings_are_empty()
    {
        var settings = new MemorySettings();
        var service = new ThemeService(
            new ThemeJsRuntime("dark", "#4c8bf5", storedTheme: "light", storedAccent: "red"),
            settings);

        await service.InitializeAsync();

        Assert.Equal("light", service.Theme);
        Assert.Equal("red", service.Accent);
        Assert.Equal("light", settings.Values[DysonAppSettingKeys.UiTheme]);
        Assert.Equal("red", settings.Values[DysonAppSettingKeys.UiAccent]);
    }

    private sealed class MemorySettings : IDysonSubjectSettingsRepository
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task<VoidResult<string>> EnsureSubjectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<Result<string?, string>> GetSettingAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            Values.TryGetValue(key, out var value);
            return Task.FromResult(Result<string?, string>.AsValue(value));
        }

        public Task<VoidResult<string>> SetSettingAsync(
            string key,
            string? value,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(value))
                Values.Remove(key);
            else
                Values[key] = value;
            return Task.FromResult(VoidResult<string>.Success);
        }
    }

    private sealed class ThemeJsRuntime(
        string theme,
        string accent,
        string? storedTheme = null,
        string? storedAccent = null) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            object? value = identifier switch
            {
                "dysonTheme.get" => storedTheme is null && storedAccent is null
                    ? null
                    : new { theme = storedTheme, accent = storedAccent },
                "dysonTheme.getResolved" => new { theme, accentHex = accent },
                "dysonTheme.apply" => null,
                "dysonTheme.set" => null,
                _ => throw new InvalidOperationException($"Unexpected JS call: {identifier}"),
            };

            if (value is null)
                return ValueTask.FromResult(default(TValue)!);

            var json = JsonSerializer.Serialize(value);
            return ValueTask.FromResult(JsonSerializer.Deserialize<TValue>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
        }
    }

    private sealed class UnavailableJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("JS interop is unavailable in this test.");

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            throw new InvalidOperationException("JS interop is unavailable in this test.");
    }
}
