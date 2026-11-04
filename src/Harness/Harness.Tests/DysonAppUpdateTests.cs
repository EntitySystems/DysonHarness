using Harness.UI.Services;

namespace Harness.Tests;

/// <summary>CalVer parsing and GitHub release selection for the in-app updater.</summary>
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

    [Fact]
    public void SelectNewestRelease_PreviewOsxArm64PkgIgnoresMsiAndOtherArchitecture()
    {
        const string json = """
            [
              {
                "tag_name": "2026.11.168",
                "html_url": "https://github.com/EntitySystems/DysonHarness/releases/tag/2026.11.168",
                "draft": false,
                "prerelease": true,
                "assets": [
                  { "name": "DysonHarness-2026.11.168-win-x64.msi",
                    "browser_download_url": "https://example.invalid/newer.msi", "size": 9 },
                  { "name": "DysonHarness-2026.11.168-osx-x64.pkg",
                    "browser_download_url": "https://example.invalid/newer-x64.pkg", "size": 8 }
                ]
              },
              {
                "tag_name": "2026.11.167",
                "html_url": "https://github.com/EntitySystems/DysonHarness/releases/tag/2026.11.167",
                "draft": false,
                "prerelease": true,
                "assets": [
                  { "name": "DysonHarness-2026.11.167-win-x64.msi",
                    "browser_download_url": "https://example.invalid/msi", "size": 7 },
                  { "name": "DysonHarness-2026.11.167-osx-x64.pkg",
                    "browser_download_url": "https://example.invalid/x64.pkg", "size": 6 },
                  { "name": "DysonHarness-2026.11.167-osx-arm64.pkg",
                    "browser_download_url": "https://example.invalid/arm64.pkg", "size": 5 }
                ]
              },
              {
                "tag_name": "2026.11.200",
                "html_url": "https://github.com/EntitySystems/DysonHarness/releases/tag/2026.11.200",
                "draft": false,
                "prerelease": false,
                "assets": [
                  { "name": "DysonHarness-2026.11.200-osx-arm64.pkg",
                    "browser_download_url": "https://example.invalid/stable-arm64.pkg", "size": 4 }
                ]
              }
            ]
            """;

        var arm = DysonGitHubReleaseClient.SelectNewestRelease(
            json, "preview", DysonGitHubReleaseClient.MacPkgAssetSuffix(DysonGitHubReleaseClient.MacArm64Rid));
        Assert.NotNull(arm);
        Assert.Equal("2026.11.167", arm.TagName);
        Assert.Equal("DysonHarness-2026.11.167-osx-arm64.pkg", arm.AssetName);
        Assert.Equal("https://example.invalid/arm64.pkg", arm.DownloadUrl);

        var x64 = DysonGitHubReleaseClient.SelectNewestRelease(
            json, "preview", DysonGitHubReleaseClient.MacPkgAssetSuffix(DysonGitHubReleaseClient.MacX64Rid));
        Assert.NotNull(x64);
        Assert.Equal("2026.11.168", x64.TagName);
        Assert.Equal("DysonHarness-2026.11.168-osx-x64.pkg", x64.AssetName);
    }

    [Fact]
    public void WindowsMsiAssetSuffix_Arm64IsDistinctAndEverythingElseStaysX64()
    {
        Assert.Equal("-win-arm64.msi", DysonGitHubReleaseClient.WindowsMsiAssetSuffix("win-arm64"));
        Assert.Equal("-win-arm64.msi", DysonGitHubReleaseClient.WindowsMsiAssetSuffix("WIN-ARM64"));
        Assert.Equal(DysonGitHubReleaseClient.MsiAssetSuffix, DysonGitHubReleaseClient.WindowsMsiAssetSuffix("win-x64"));
        Assert.Equal(DysonGitHubReleaseClient.MsiAssetSuffix, DysonGitHubReleaseClient.WindowsMsiAssetSuffix(null));
        Assert.Equal(DysonGitHubReleaseClient.MsiAssetSuffix, DysonGitHubReleaseClient.WindowsMsiAssetSuffix(""));
        Assert.Equal("-win-x64.msi", DysonGitHubReleaseClient.MsiAssetSuffix);
    }

    [Fact]
    public void InstallerAssetSuffix_WindowsFollowsProcessArchitectureNotStampedRid()
    {
        var stampedArm = new DysonAppVersionInfo { Version = "2026.8.142", Rid = "win-arm64" };
        var stampedX64 = new DysonAppVersionInfo { Version = "2026.8.142", Rid = "win-x64" };
        var expected = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64
            ? "-win-arm64.msi"
            : "-win-x64.msi";

        if (OperatingSystem.IsMacOS())
            return;

        // Both stamped rids resolve the same way: the running process decides, so an x64
        // build under emulation on an ARM device keeps the x64 MSI.
        Assert.Equal(expected, DysonAppUpdateService.InstallerAssetSuffix(stampedArm));
        Assert.Equal(expected, DysonAppUpdateService.InstallerAssetSuffix(stampedX64));
    }

    [Fact]
    public void SelectNewestRelease_WindowsArm64MsiIgnoresX64Msi()
    {
        const string json = """
            [
              {
                "tag_name": "2026.11.168",
                "html_url": "https://github.com/EntitySystems/DysonHarness/releases/tag/2026.11.168",
                "draft": false,
                "prerelease": true,
                "assets": [
                  { "name": "DysonHarness-2026.11.168-win-x64.msi",
                    "browser_download_url": "https://example.invalid/x64.msi", "size": 9 }
                ]
              },
              {
                "tag_name": "2026.11.167",
                "html_url": "https://github.com/EntitySystems/DysonHarness/releases/tag/2026.11.167",
                "draft": false,
                "prerelease": true,
                "assets": [
                  { "name": "DysonHarness-2026.11.167-win-x64.msi",
                    "browser_download_url": "https://example.invalid/old-x64.msi", "size": 8 },
                  { "name": "DysonHarness-2026.11.167-win-arm64.msi",
                    "browser_download_url": "https://example.invalid/arm64.msi", "size": 7 }
                ]
              }
            ]
            """;

        var arm = DysonGitHubReleaseClient.SelectNewestRelease(
            json, "preview", DysonGitHubReleaseClient.WindowsMsiAssetSuffix("win-arm64"));
        Assert.NotNull(arm);
        Assert.Equal("2026.11.167", arm.TagName);
        Assert.Equal("DysonHarness-2026.11.167-win-arm64.msi", arm.AssetName);
        Assert.Equal("https://example.invalid/arm64.msi", arm.DownloadUrl);

        var x64 = DysonGitHubReleaseClient.SelectNewestMsiRelease(json, "preview");
        Assert.NotNull(x64);
        Assert.Equal("2026.11.168", x64.TagName);
        Assert.Equal("DysonHarness-2026.11.168-win-x64.msi", x64.AssetName);
    }

    [Fact]
    public void MacUpdateScript_ReopensApplicationsAppWhenInstallerFails()
    {
        var script = DysonAppUpdateService.BuildMacUpdateScript("/tmp/Dyson Harness's pkg.pkg");

        Assert.Contains("pkg='/tmp/Dyson Harness'\\''s pkg.pkg'", script, StringComparison.Ordinal);
        Assert.Contains("osascript - \"$pkg\" <<'EOF' || true", script, StringComparison.Ordinal);
        Assert.Contains("quoted form of (item 1 of argv)", script, StringComparison.Ordinal);
        Assert.Contains("with administrator privileges", script, StringComparison.Ordinal);
        var installAt = script.IndexOf("do shell script", StringComparison.Ordinal);
        var openAt = script.IndexOf("open /Applications/DysonHarness.app", StringComparison.Ordinal);
        Assert.True(installAt >= 0 && openAt > installAt);
        Assert.DoesNotContain("set -e", script, StringComparison.Ordinal);

        if (!OperatingSystem.IsMacOS())
            return;

        var path = Path.Combine(Path.GetTempPath(), "dyson-pkg-update-syntax.sh");
        File.WriteAllText(path, script);
        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/bash",
                ArgumentList = { "-n", path },
                RedirectStandardError = true,
            });
            Assert.NotNull(proc);
            proc.WaitForExit();
            Assert.True(proc.ExitCode == 0, proc.StandardError.ReadToEnd());
        }
        finally
        {
            File.Delete(path);
        }
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
