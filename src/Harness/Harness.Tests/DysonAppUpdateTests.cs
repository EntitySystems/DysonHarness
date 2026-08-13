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
        var release = new DysonAppVersionInfo { Version = "2026.8.142", InformationalVersion = "2026.8.142+abc1234", Channel = "preview" };

        Assert.False(dev.IsStampedRelease);
        Assert.True(release.IsStampedRelease);
        Assert.Equal(DysonAppVersionInfo.DefaultRepo, dev.EffectiveRepo);
        Assert.Null(dev.ChannelBadge);
        Assert.Equal("preview", release.ChannelBadge);
    }

    [Fact]
    public void EffectiveChannel_DefaultsMissingAndUnknownToPreview()
    {
        Assert.Equal("preview", new DysonAppVersionInfo { Version = "2026.8.1" }.EffectiveChannel);
        Assert.Equal("preview", new DysonAppVersionInfo { Version = "2026.8.1", Channel = "" }.EffectiveChannel);
        Assert.Equal("preview", new DysonAppVersionInfo { Version = "2026.8.1", Channel = "nightly" }.EffectiveChannel);
        Assert.Equal("preview", DysonAppVersionInfo.NormalizeChannel(null));
        Assert.Equal("stable", new DysonAppVersionInfo { Version = "2026.8.1", Channel = "Stable" }.EffectiveChannel);
        Assert.Equal("stable", DysonAppVersionInfo.NormalizeChannel("STABLE"));
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
                  "channel": "stable",
                  "rid": "win-x64",
                  "repo": "EntitySystems/DysonHarness"
                }
                """);

            var info = DysonAppVersionInfo.ReadFrom(dir);
            Assert.NotNull(info);
            Assert.Equal("2026.8.142", info.Version);
            Assert.Equal("stable", info.Channel);
            Assert.Equal("stable", info.EffectiveChannel);
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
    public void SelectNewestMsiRelease_PicksHighestCalVerOnPreviewTrack()
    {
        var release = DysonGitHubReleaseClient.SelectNewestMsiRelease(SampleReleasesJson, "preview");

        Assert.NotNull(release);
        Assert.Equal("2026.8.142", release.TagName);
        Assert.Equal("DysonHarness-2026.8.142-win-x64.msi", release.AssetName);
        Assert.Equal("https://example.invalid/2026.8.142/DysonHarness-2026.8.142-win-x64.msi", release.DownloadUrl);
        Assert.Equal(52_428_800, release.SizeBytes);
        Assert.Equal(new Uri("https://github.com/EntitySystems/DysonHarness/releases/tag/2026.8.142"), release.ReleasePageUrl);
    }

    [Fact]
    public void SelectNewestMsiRelease_StableTrackIgnoresPrereleases()
    {
        var release = DysonGitHubReleaseClient.SelectNewestMsiRelease(SampleReleasesJson, "stable");

        Assert.NotNull(release);
        Assert.Equal("2026.8.100", release.TagName);
        Assert.Equal("DysonHarness-2026.8.100-win-x64.msi", release.AssetName);
    }

    [Fact]
    public void SelectNewestMsiRelease_PreviewTrackIgnoresStableReleases()
    {
        // Highest overall tag is stable 2026.8.200; preview must stay on the prerelease track.
        var json = """
            [
              {
                "tag_name": "2026.8.200",
                "html_url": "https://github.com/EntitySystems/DysonHarness/releases/tag/2026.8.200",
                "draft": false,
                "prerelease": false,
                "assets": [
                  { "name": "DysonHarness-2026.8.200-win-x64.msi",
                    "browser_download_url": "https://example.invalid/stable.msi", "size": 9 }
                ]
              },
              {
                "tag_name": "2026.8.150",
                "html_url": "https://github.com/EntitySystems/DysonHarness/releases/tag/2026.8.150",
                "draft": false,
                "prerelease": true,
                "assets": [
                  { "name": "DysonHarness-2026.8.150-win-x64.msi",
                    "browser_download_url": "https://example.invalid/preview.msi", "size": 8 }
                ]
              }
            ]
            """;

        var preview = DysonGitHubReleaseClient.SelectNewestMsiRelease(json, "preview");
        Assert.NotNull(preview);
        Assert.Equal("2026.8.150", preview.TagName);

        var stable = DysonGitHubReleaseClient.SelectNewestMsiRelease(json, "stable");
        Assert.NotNull(stable);
        Assert.Equal("2026.8.200", stable.TagName);
    }

    [Fact]
    public void SelectNewestMsiRelease_SkipsMissingOrMalformedReleasePageUrls()
    {
        const string missingUrlJson = """
            [{ "tag_name": "2026.8.150", "draft": false, "prerelease": true,
               "assets": [{ "name": "DysonHarness-2026.8.150-win-x64.msi",
                            "browser_download_url": "https://example.invalid/a.msi", "size": 1 }] }]
            """;
        const string malformedUrlJson = """
            [
              { "tag_name": "2026.8.151", "draft": false, "prerelease": true,
                "html_url": "https://github.com/EntitySystems/DysonHarness/releases/tag/another-tag",
                "assets": [{ "name": "DysonHarness-2026.8.151-win-x64.msi",
                             "browser_download_url": "https://example.invalid/mismatched.msi", "size": 3 }] },
              { "tag_name": "2026.8.150", "draft": false, "prerelease": true,
                "html_url": "https://example.invalid/not-github",
                "assets": [{ "name": "DysonHarness-2026.8.150-win-x64.msi",
                             "browser_download_url": "https://example.invalid/new.msi", "size": 2 }] },
              { "tag_name": "2026.8.149", "draft": false, "prerelease": true,
                "html_url": "https://github.com/EntitySystems/DysonHarness/releases/tag/2026.8.149",
                "assets": [{ "name": "DysonHarness-2026.8.149-win-x64.msi",
                             "browser_download_url": "https://example.invalid/old.msi", "size": 1 }] }
            ]
            """;

        Assert.Null(DysonGitHubReleaseClient.SelectNewestMsiRelease(missingUrlJson));

        var release = DysonGitHubReleaseClient.SelectNewestMsiRelease(malformedUrlJson);
        Assert.NotNull(release);
        Assert.Equal("2026.8.149", release.TagName);
        Assert.Equal(new Uri("https://github.com/EntitySystems/DysonHarness/releases/tag/2026.8.149"), release.ReleasePageUrl);
    }

    [Fact]
    public async Task CheckManuallyAsync_ReturnsReleaseForUnstampedBuildWithoutChangingUpdateState()
    {
        var handler = new StubHttpHandler(_ => Json(SampleReleasesJson));
        using var http = new HttpClient(handler);
        var service = new DysonAppUpdateService(http);

        var first = await service.CheckManuallyAsync();
        var second = await service.CheckManuallyAsync();

        Assert.False(first.IsError);
        Assert.False(second.IsError);
        Assert.NotNull(first.Value.NewestRelease);
        Assert.Equal("2026.8.142", first.Value.NewestRelease.TagName);
        Assert.Equal(DysonAppVersionInfo.Local.Version, first.Value.LocalVersion);
        Assert.Equal(DysonAppVersionInfo.Local.EffectiveChannel, first.Value.LocalChannel);
        Assert.False(first.Value.IsNewerStampedRelease);
        Assert.False(first.Value.IsInAppInstallEligible);
        Assert.Equal(DysonAppUpdatePhase.Idle, service.Phase);
        Assert.Null(service.AvailableVersion);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void SelectNewestMsiRelease_ReturnsNullForEmptyOrMalformedPayloads()
    {
        Assert.Null(DysonGitHubReleaseClient.SelectNewestMsiRelease(""));
        Assert.Null(DysonGitHubReleaseClient.SelectNewestMsiRelease("[]"));
        Assert.Null(DysonGitHubReleaseClient.SelectNewestMsiRelease("{ not json"));
        Assert.Null(DysonGitHubReleaseClient.SelectNewestMsiRelease("""{"tag_name":"2026.8.142"}"""));
        Assert.Null(DysonGitHubReleaseClient.SelectNewestMsiRelease("""
            [{ "tag_name": "2026.8.150", "draft": false, "prerelease": true,
               "assets": [{ "name": "DysonHarness-2026.8.150-linux-x64.zip",
                            "browser_download_url": "https://example.invalid/a.zip", "size": 1 }] }]
            """));
    }

    /// <summary>Draft releases and zip-only releases must be skipped even when their tag is highest.</summary>
    private const string SampleReleasesJson = """
        [
          {
            "tag_name": "2026.8.150",
            "html_url": "https://github.com/EntitySystems/DysonHarness/releases/tag/2026.8.150",
            "draft": true,
            "prerelease": true,
            "assets": [
              { "name": "DysonHarness-2026.8.150-win-x64.msi",
                "browser_download_url": "https://example.invalid/draft.msi", "size": 1 }
            ]
          },
          {
            "tag_name": "2026.8.149",
            "html_url": "https://github.com/EntitySystems/DysonHarness/releases/tag/2026.8.149",
            "draft": false,
            "prerelease": true,
            "assets": [
              { "name": "DysonHarness-2026.8.149-win-x64.zip",
                "browser_download_url": "https://example.invalid/zip-only.zip", "size": 2 }
            ]
          },
          {
            "tag_name": "2026.8.99",
            "html_url": "https://github.com/EntitySystems/DysonHarness/releases/tag/2026.8.99",
            "draft": false,
            "prerelease": true,
            "assets": [
              { "name": "DysonHarness-2026.8.99-win-x64.msi",
                "browser_download_url": "https://example.invalid/old.msi", "size": 3 }
            ]
          },
          {
            "tag_name": "2026.8.142",
            "html_url": "https://github.com/EntitySystems/DysonHarness/releases/tag/2026.8.142",
            "draft": false,
            "prerelease": true,
            "assets": [
              { "name": "DysonHarness-2026.8.142-linux-x64.zip",
                "browser_download_url": "https://example.invalid/2026.8.142/linux.zip", "size": 10 },
              { "name": "DysonHarness-2026.8.142-win-x64.msi",
                "browser_download_url": "https://example.invalid/2026.8.142/DysonHarness-2026.8.142-win-x64.msi",
                "size": 52428800 }
            ]
          },
          {
            "tag_name": "2026.8.100",
            "html_url": "https://github.com/EntitySystems/DysonHarness/releases/tag/2026.8.100",
            "draft": false,
            "prerelease": false,
            "assets": [
              { "name": "DysonHarness-2026.8.100-win-x64.msi",
                "browser_download_url": "https://example.invalid/2026.8.100/DysonHarness-2026.8.100-win-x64.msi",
                "size": 40 }
            ]
          }
        ]
        """;

    private static HttpResponseMessage Json(string json) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(responder(request));
        }
    }
}
