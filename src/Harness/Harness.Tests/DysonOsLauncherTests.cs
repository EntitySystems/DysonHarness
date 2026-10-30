using DysonHarness;

namespace Harness.Tests;

/// <summary>OS launcher rejection paths only. Success paths would open Explorer or a browser.</summary>
public sealed class DysonOsLauncherTests
{
    [Fact]
    public async Task OpenFileAsync_rejects_empty_and_missing_file()
    {
        var empty = await DysonOsLauncher.OpenFileAsync("  ");
        Assert.True(empty.IsError);
        Assert.Equal("Path is empty.", empty.Error);

        var missing = Path.Combine(Path.GetTempPath(), "dyson-os-launch-" + Guid.NewGuid().ToString("N"));
        var gone = await DysonOsLauncher.OpenFileAsync(missing);
        Assert.True(gone.IsError);
        Assert.Equal("File does not exist.", gone.Error);
    }

    [Fact]
    public async Task OpenFolderAsync_rejects_empty_and_missing_directory()
    {
        var empty = await DysonOsLauncher.OpenFolderAsync("  ");
        Assert.True(empty.IsError);
        Assert.Equal("Path is empty.", empty.Error);

        var missing = Path.Combine(Path.GetTempPath(), "dyson-os-launch-" + Guid.NewGuid().ToString("N"));
        var gone = await DysonOsLauncher.OpenFolderAsync(missing);
        Assert.True(gone.IsError);
        Assert.Equal("Directory does not exist.", gone.Error);
    }

    [Fact]
    public async Task OpenUrlAsync_rejects_non_http_schemes()
    {
        foreach (var url in new[] { "", "javascript:alert(1)", "file:///c:/x", "ftp://example.com", "example.com" })
        {
            var opened = await DysonOsLauncher.OpenUrlAsync(url);
            Assert.True(opened.IsError, url);
        }
    }
}
