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

    private sealed class ThemeJsRuntime(string theme, string accent) : IJSRuntime
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
                "dysonTheme.get" => null,
                "dysonTheme.getResolved" => new { theme, accentHex = accent },
                "dysonTheme.apply" => null,
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
