using Harness.UI.Services;

namespace Harness.Tests;

/// <summary>CalVer parsing and GitHub release selection for the Windows in-app updater.</summary>
public class DysonAppUpdateTests
{
    [Fact]
    public void ParseCalVer_HandlesTagAndInformationalForms()
    {
        Assert.Equal(new Version(2026, 8, 142), DysonAppVersionInfo.ParseCalVer("2026.8.142"));
        Assert.Equal(new Version(2026, 8, 142), DysonAppVersionInfo.ParseCalVer(" v2026.8.142 "));
        Assert.Equal(new Version(2026, 8, 142), DysonAppVersionInfo.ParseCalVer("2026.8.142+abc1234"));
        Assert.Equal(new Version(2026, 8, 0), DysonAppVersionInfo.ParseCalVer("2026.8"));
        Assert.Null(DysonAppVersionInfo.ParseCalVer("nightly"));
        Assert.Null(DysonAppVersionInfo.ParseCalVer(""));
        Assert.Null(DysonAppVersionInfo.ParseCalVer(null));
    }

    [Fact]
    public void IsNewer_ComparesCalVerSegmentsNumerically()
    {
        Assert.True(DysonAppVersionInfo.IsNewer("2026.8.142", "2026.8.99"));
        Assert.True(DysonAppVersionInfo.IsNewer("2026.10.1", "2026.9.999"));
        Assert.True(DysonAppVersionInfo.IsNewer("2027.1.1", "2026.12.500"));
        Assert.False(DysonAppVersionInfo.IsNewer("2026.8.142", "2026.8.142"));
        Assert.False(DysonAppVersionInfo.IsNewer("2026.8.99", "2026.8.142"));
        // Unstamped local builds must never be treated as updatable by tag comparison alone.
        Assert.False(DysonAppVersionInfo.IsNewer("2026.8.142", "not-a-version"));
        Assert.False(DysonAppVersionInfo.IsNewer("latest", "2026.8.142"));
    }

    [Fact]
    public void LocalDevBuild_IsNotAStampedRelease()
    {
        var dev = new DysonAppVersionInfo { Version = "1.0.0", InformationalVersion = "1.0.0" };
        var release = new DysonAppVersionInfo { Version = "2026.8.142", InformationalVersion = "2026.8.142+abc1234" };

        Assert.False(dev.IsStampedRelease);
        Assert.True(release.IsStampedRelease);
        Assert.Equal(DysonAppVersionInfo.DefaultRepo, dev.EffectiveRepo);
    }

    [Fact]
    public void ReadFrom_ParsesVersionJson_AndIgnoresMissingOrEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dyson-version-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(DysonAppVersionInfo.ReadFrom(dir));

            File.WriteAllText(Path.Combine(dir, DysonAppVersionInfo.FileName), """
                {
                  "version": "2026.8.142",
                  "informationalVersion": "2026.8.142+abc1234",
                  "rid": "win-x64",
                  "repo": "EntitySystems/DysonHarness"
                }
                """);

            var info = DysonAppVersionInfo.ReadFrom(dir);
            Assert.NotNull(info);
            Assert.Equal("2026.8.142", info.Version);
            Assert.Equal("win-x64", info.Rid);
            Assert.True(info.IsStampedRelease);

            File.WriteAllText(Path.Combine(dir, DysonAppVersionInfo.FileName), "not json");
            Assert.Null(DysonAppVersionInfo.ReadFrom(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SelectNewestMsiRelease_PicksHighestCalVerWithWindowsAsset()
    {
        var release = DysonGitHubReleaseClient.SelectNewestMsiRelease(SampleReleasesJson);

        Assert.NotNull(release);
        Assert.Equal("2026.8.142", release.TagName);
        Assert.Equal("DysonHarness-2026.8.142-win-x64.msi", release.AssetName);
        Assert.Equal("https://example.invalid/2026.8.142/DysonHarness-2026.8.142-win-x64.msi", release.DownloadUrl);
        Assert.Equal(52_428_800, release.SizeBytes);
    }

    [Fact]
    public void SelectNewestMsiRelease_ReturnsNullForEmptyOrMalformedPayloads()
    {
        Assert.Null(DysonGitHubReleaseClient.SelectNewestMsiRelease(""));
        Assert.Null(DysonGitHubReleaseClient.SelectNewestMsiRelease("[]"));
        Assert.Null(DysonGitHubReleaseClient.SelectNewestMsiRelease("{ not json"));
        Assert.Null(DysonGitHubReleaseClient.SelectNewestMsiRelease("""{"tag_name":"2026.8.142"}"""));
        Assert.Null(DysonGitHubReleaseClient.SelectNewestMsiRelease("""
            [{ "tag_name": "2026.8.150", "draft": false,
               "assets": [{ "name": "DysonHarness-2026.8.150-linux-x64.zip",
                            "browser_download_url": "https://example.invalid/a.zip", "size": 1 }] }]
            """));
    }

    /// <summary>Draft releases and zip-only releases must be skipped even when their tag is highest.</summary>
    private const string SampleReleasesJson = """
        [
          {
            "tag_name": "2026.8.150",
            "draft": true,
            "prerelease": true,
            "assets": [
              { "name": "DysonHarness-2026.8.150-win-x64.msi",
                "browser_download_url": "https://example.invalid/draft.msi", "size": 1 }
            ]
          },
          {
            "tag_name": "2026.8.149",
            "draft": false,
            "prerelease": true,
            "assets": [
              { "name": "DysonHarness-2026.8.149-win-x64.zip",
                "browser_download_url": "https://example.invalid/zip-only.zip", "size": 2 }
            ]
          },
          {
            "tag_name": "2026.8.99",
            "draft": false,
            "prerelease": true,
            "assets": [
              { "name": "DysonHarness-2026.8.99-win-x64.msi",
                "browser_download_url": "https://example.invalid/old.msi", "size": 3 }
            ]
          },
          {
            "tag_name": "2026.8.142",
            "draft": false,
            "prerelease": true,
            "assets": [
              { "name": "DysonHarness-2026.8.142-linux-x64.zip",
                "browser_download_url": "https://example.invalid/2026.8.142/linux.zip", "size": 10 },
              { "name": "DysonHarness-2026.8.142-win-x64.msi",
                "browser_download_url": "https://example.invalid/2026.8.142/DysonHarness-2026.8.142-win-x64.msi",
                "size": 52428800 }
            ]
          }
        ]
        """;
}
