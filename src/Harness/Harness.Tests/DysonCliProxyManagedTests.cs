using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DysonHarness;

namespace Harness.Tests;

public class DysonCliProxyAssetResolverTests
{
    [Theory]
    [InlineData("7.2.102", "windows", Architecture.X64, "CLIProxyAPI_7.2.102_windows_amd64.zip")]
    [InlineData("7.2.102", "windows", Architecture.Arm64, "CLIProxyAPI_7.2.102_windows_aarch64.zip")]
    [InlineData("7.2.102", "linux", Architecture.X64, "CLIProxyAPI_7.2.102_linux_amd64.tar.gz")]
    [InlineData("7.2.102", "linux", Architecture.Arm64, "CLIProxyAPI_7.2.102_linux_aarch64.tar.gz")]
    [InlineData("7.2.102", "darwin", Architecture.X64, "CLIProxyAPI_7.2.102_darwin_amd64.tar.gz")]
    [InlineData("7.2.102", "darwin", Architecture.Arm64, "CLIProxyAPI_7.2.102_darwin_aarch64.tar.gz")]
    public void ResolveAssetFileName_maps_os_arch(string version, string os, Architecture arch, string expected)
    {
        var platform = os switch
        {
            "windows" => OSPlatform.Windows,
            "linux" => OSPlatform.Linux,
            "darwin" => OSPlatform.OSX,
            _ => throw new ArgumentOutOfRangeException(nameof(os)),
        };

        var name = DysonCliProxyAssetResolver.ResolveAssetFileName(version, platform, arch);
        Assert.Equal(expected, name);
    }

    [Fact]
    public void ResolveDownloadUrl_uses_pinned_download_base()
    {
        var url = DysonCliProxyAssetResolver.ResolveDownloadUrl(
            "7.2.102", OSPlatform.Windows, Architecture.X64);
        Assert.Equal(
            "https://github.com/router-for-me/CLIProxyAPI/releases/download/v7.2.102/CLIProxyAPI_7.2.102_windows_amd64.zip",
            url);
    }
}

public class DysonThirdPartyResourcesTests
{
    [Fact]
    public void CliProxyApi_parses_tag_and_version_from_release_url()
    {
        Assert.Equal("v7.2.102", DysonThirdPartyResources.CliProxyApi.Tag);
        Assert.Equal("7.2.102", DysonThirdPartyResources.CliProxyApi.Version);
        Assert.Equal(
            "https://github.com/router-for-me/CLIProxyAPI/releases/download/v7.2.102/",
            DysonThirdPartyResources.CliProxyApi.DownloadBaseUrl);
    }

    [Theory]
    [InlineData("https://github.com/router-for-me/CLIProxyAPI/releases/tag/v7.2.102", "v7.2.102")]
    [InlineData("https://github.com/router-for-me/CLIProxyAPI/releases/tag/v7.2.102/", "v7.2.102")]
    [InlineData("https://example.com/releases/tag/1.0.0", "1.0.0")]
    public void ParseTag_reads_final_path_segment(string url, string expected)
    {
        Assert.Equal(expected, DysonThirdPartyResources.CliProxyApi.ParseTag(url));
    }
}

public class ManagedSlugSyncTests
{
    [Fact]
    public void NormalizeModelId_strips_parenthetical_suffix()
    {
        Assert.Equal("gpt-5.4", ManagedInferenceProviderBase.NormalizeModelId("gpt-5.4 (codex)"));
        Assert.Equal("grok-4", ManagedInferenceProviderBase.NormalizeModelId("  grok-4  "));
        Assert.Equal("", ManagedInferenceProviderBase.NormalizeModelId(null));
    }

    [Fact]
    public void MapModelsToSlugs_filters_by_owner_tokens_and_dedupes()
    {
        var models = new[]
        {
            new ManagedModelInfo("gpt-5.4 (pro)", "codex", "codex-pro", "GPT 5.4"),
            new ManagedModelInfo("gpt-5.4", "openai", null, null),
            new ManagedModelInfo("claude-sonnet", "anthropic", "claude", null),
            new ManagedModelInfo("grok-4", "xai", "xai", "Grok 4"),
        };

        var codex = ManagedInferenceProviderBase.MapModelsToSlugs(models, ["codex", "openai"]);
        Assert.Equal(["gpt-5.4"], codex.Select(s => s.Slug).ToArray());
        Assert.Equal(ManagedInferenceProviderBase.DefaultReasoningModes, codex[0].ReasoningModes);
        Assert.Equal("high", codex[0].DefaultReasoningEffort);

        var grok = ManagedInferenceProviderBase.MapModelsToSlugs(models, ["xai", "grok"]);
        Assert.Single(grok);
        Assert.Equal("grok-4", grok[0].Slug);
    }

    [Fact]
    public void FindSha256_parses_checksums_txt()
    {
        var txt = """
            abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789  CLIProxyAPI_7.2.102_windows_amd64.zip
            deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef *CLIProxyAPI_7.2.102_linux_amd64.tar.gz
            """;

        Assert.Equal(
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            DysonCliProxyDownloader.FindSha256(txt, "CLIProxyAPI_7.2.102_windows_amd64.zip"));
        Assert.Equal(
            "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
            DysonCliProxyDownloader.FindSha256(txt, "CLIProxyAPI_7.2.102_linux_amd64.tar.gz"));
        Assert.Null(DysonCliProxyDownloader.FindSha256(txt, "missing.zip"));
    }
}

public class ManagedCodexOAuthPreflightTests
{
    [Fact]
    public void AuthUrlPath_requests_webui_oauth_forwarder()
    {
        Assert.Equal("codex-auth-url?is_webui=true", ManagedCodexInferenceProvider.CodexAuthUrlPath);
        Assert.Contains("is_webui=true", ManagedCodexInferenceProvider.CodexAuthUrlPath, StringComparison.Ordinal);
    }

    [Fact]
    public void TryEnsureOAuthCallbackPortFree_errors_when_1455_occupied()
    {
        var holder = new TcpListener(IPAddress.Loopback, ManagedCodexInferenceProvider.CodexOAuthCallbackPort);
        holder.Start();
        try
        {
            var result = ManagedCodexInferenceProvider.TryEnsureOAuthCallbackPortFree();
            Assert.True(result.IsError);
            Assert.Contains("1455", result.Error, StringComparison.Ordinal);
            Assert.Contains("free", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            holder.Stop();
        }
    }

    [Fact]
    public void TryEnsureOAuthCallbackPortFree_succeeds_when_1455_available()
    {
        // If something else holds 1455 on the machine, skip rather than flake.
        TcpListener? probe = null;
        try
        {
            probe = new TcpListener(IPAddress.Loopback, ManagedCodexInferenceProvider.CodexOAuthCallbackPort);
            probe.Start();
            probe.Stop();
            probe = null;
        }
        catch (SocketException)
        {
            return;
        }
        finally
        {
            probe?.Stop();
        }

        var result = ManagedCodexInferenceProvider.TryEnsureOAuthCallbackPortFree();
        Assert.False(result.IsError);
    }
}
