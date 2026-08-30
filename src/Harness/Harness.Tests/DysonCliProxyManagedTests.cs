using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DysonHarness;

namespace Harness.Tests;

public class DysonCliProxyAssetResolverTests
{
    [Theory]
    [InlineData("7.2.145", "windows", Architecture.X64, "CLIProxyAPI_7.2.145_windows_amd64.zip")]
    [InlineData("7.2.145", "windows", Architecture.Arm64, "CLIProxyAPI_7.2.145_windows_aarch64.zip")]
    [InlineData("7.2.145", "linux", Architecture.X64, "CLIProxyAPI_7.2.145_linux_amd64.tar.gz")]
    [InlineData("7.2.145", "linux", Architecture.Arm64, "CLIProxyAPI_7.2.145_linux_aarch64.tar.gz")]
    [InlineData("7.2.145", "darwin", Architecture.X64, "CLIProxyAPI_7.2.145_darwin_amd64.tar.gz")]
    [InlineData("7.2.145", "darwin", Architecture.Arm64, "CLIProxyAPI_7.2.145_darwin_aarch64.tar.gz")]
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
            "7.2.145", OSPlatform.Windows, Architecture.X64);
        Assert.Equal(
            "https://github.com/router-for-me/CLIProxyAPI/releases/download/v7.2.145/CLIProxyAPI_7.2.145_windows_amd64.zip",
            url);
    }
}

public class DysonThirdPartyResourcesTests
{
    [Fact]
    public void CliProxyApi_parses_tag_and_version_from_release_url()
    {
        Assert.Equal("v7.2.145", DysonThirdPartyResources.CliProxyApi.Tag);
        Assert.Equal("7.2.145", DysonThirdPartyResources.CliProxyApi.Version);
        Assert.Equal(
            "https://github.com/router-for-me/CLIProxyAPI/releases/download/v7.2.145/",
            DysonThirdPartyResources.CliProxyApi.DownloadBaseUrl);
    }

    [Theory]
    [InlineData("https://github.com/router-for-me/CLIProxyAPI/releases/tag/v7.2.145", "v7.2.145")]
    [InlineData("https://github.com/router-for-me/CLIProxyAPI/releases/tag/v7.2.145/", "v7.2.145")]
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
            new ManagedModelInfo("gemini-2.5-pro", "google", "gemini", null),
            new ManagedModelInfo("antigravity-flash", "antigravity", null, null),
            new ManagedModelInfo("kimi-k2.5", "moonshot", "kimi", "Kimi K2.5"),
            new ManagedModelInfo("kimi-k2.5", "kimi", null, null),
        };

        var codex = ManagedInferenceProviderBase.MapModelsToSlugs(models, ["codex", "openai"]);
        Assert.Equal(["gpt-5.4"], codex.Select(s => s.Slug).ToArray());
        Assert.Equal(ManagedInferenceProviderBase.DefaultReasoningModes, codex[0].ReasoningModes);
        Assert.Equal("high", codex[0].DefaultReasoningEffort);

        var grok = ManagedInferenceProviderBase.MapModelsToSlugs(models, ["xai", "grok"]);
        Assert.Single(grok);
        Assert.Equal("grok-4", grok[0].Slug);

        var antigravity = ManagedInferenceProviderBase.MapModelsToSlugs(
            models, ["antigravity", "google", "gemini"]);
        Assert.Equal(["gemini-2.5-pro", "antigravity-flash"], antigravity.Select(s => s.Slug).ToArray());

        var kimi = ManagedInferenceProviderBase.MapModelsToSlugs(models, ["kimi", "moonshot"]);
        Assert.Equal(["kimi-k2.5"], kimi.Select(s => s.Slug).ToArray());

        var claude = ManagedInferenceProviderBase.MapModelsToSlugs(models, ["claude", "anthropic"]);
        Assert.Equal(["claude-sonnet"], claude.Select(s => s.Slug).ToArray());
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

public class ManagedAntigravityOAuthPreflightTests
{
    [Fact]
    public void AuthUrlPath_requests_webui_oauth_forwarder()
    {
        Assert.Equal(
            "antigravity-auth-url?is_webui=true",
            ManagedAntigravityInferenceProvider.AntigravityAuthUrlPath);
        Assert.Contains(
            "is_webui=true",
            ManagedAntigravityInferenceProvider.AntigravityAuthUrlPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TryEnsureOAuthCallbackPortFree_errors_when_51121_occupied()
    {
        var holder = new TcpListener(
            IPAddress.Loopback,
            ManagedAntigravityInferenceProvider.AntigravityOAuthCallbackPort);
        holder.Start();
        try
        {
            var result = ManagedAntigravityInferenceProvider.TryEnsureOAuthCallbackPortFree();
            Assert.True(result.IsError);
            Assert.Contains("51121", result.Error, StringComparison.Ordinal);
            Assert.Contains("free", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            holder.Stop();
        }
    }

    [Fact]
    public void TryEnsureOAuthCallbackPortFree_succeeds_when_51121_available()
    {
        TcpListener? probe = null;
        try
        {
            probe = new TcpListener(
                IPAddress.Loopback,
                ManagedAntigravityInferenceProvider.AntigravityOAuthCallbackPort);
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

        var result = ManagedAntigravityInferenceProvider.TryEnsureOAuthCallbackPortFree();
        Assert.False(result.IsError);
    }
}

public class ManagedClaudeOAuthPreflightTests
{
    [Fact]
    public void AuthUrlPath_requests_webui_oauth_forwarder()
    {
        Assert.Equal(
            "anthropic-auth-url?is_webui=true",
            ManagedClaudeInferenceProvider.ClaudeAuthUrlPath);
        Assert.Contains(
            "is_webui=true",
            ManagedClaudeInferenceProvider.ClaudeAuthUrlPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TryEnsureOAuthCallbackPortFree_errors_when_54545_occupied()
    {
        var holder = new TcpListener(
            IPAddress.Loopback,
            ManagedClaudeInferenceProvider.ClaudeOAuthCallbackPort);
        holder.Start();
        try
        {
            var result = ManagedClaudeInferenceProvider.TryEnsureOAuthCallbackPortFree();
            Assert.True(result.IsError);
            Assert.Contains("54545", result.Error, StringComparison.Ordinal);
            Assert.Contains("free", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            holder.Stop();
        }
    }

    [Fact]
    public void TryEnsureOAuthCallbackPortFree_succeeds_when_54545_available()
    {
        TcpListener? probe = null;
        try
        {
            probe = new TcpListener(
                IPAddress.Loopback,
                ManagedClaudeInferenceProvider.ClaudeOAuthCallbackPort);
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

        var result = ManagedClaudeInferenceProvider.TryEnsureOAuthCallbackPortFree();
        Assert.False(result.IsError);
    }
}

public class DysonCliProxySharedSecretsTests
{
    [Fact]
    public async Task GetApiKey_and_GetManagementKey_return_hardcoded_constants()
    {
        using var http = new HttpClient();
        await using var host = new DysonCliProxyHost(http);

        Assert.Equal("dyson-cliproxy-local-api-key-v1", DysonCliProxyHost.DefaultApiKey);
        Assert.Equal("dyson-cliproxy-local-mgmt-key-v1", DysonCliProxyHost.DefaultManagementKey);

        var apiKey = host.GetApiKey();
        Assert.False(apiKey.IsError);
        Assert.Equal(DysonCliProxyHost.DefaultApiKey, apiKey.Value);

        var managementKey = host.GetManagementKey();
        Assert.False(managementKey.IsError);
        Assert.Equal(DysonCliProxyHost.DefaultManagementKey, managementKey.Value);
    }

    [Fact]
    public void OpenAiCompatibleAgentProvider_managed_source_forces_cliproxy_constants()
    {
        const string staleKey = "old-random-key";
        const string staleUrl = "http://127.0.0.1:9999/v1";

        var managed = new DysonModelProviderEntity
        {
            DisplayName = "Managed Codex",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            ManagedSource = "cliproxy-codex",
            ApiKey = staleKey,
            BaseUrl = staleUrl,
        };
        var managedProvider = new OpenAiCompatibleAgentProvider(managed, slug: null);
        Assert.Equal(DysonCliProxyHost.DefaultApiKey, managedProvider.ApiKey);
        Assert.Equal(DysonCliProxyHost.DefaultLocalBaseUrl, managedProvider.BaseUrl);

        var userOwned = new DysonModelProviderEntity
        {
            DisplayName = "User OpenAI",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            ApiKey = staleKey,
            BaseUrl = staleUrl,
        };
        var userProvider = new OpenAiCompatibleAgentProvider(userOwned, slug: null);
        Assert.Equal(staleKey, userProvider.ApiKey);
        Assert.Equal(staleUrl, userProvider.BaseUrl);
    }

    [Fact]
    public void OpenAiCompatibleAgentProvider_openrouter_managed_source_keeps_credentials()
    {
        const string apiKey = "sk-or-user-key";
        const string baseUrl = "https://openrouter.ai/api/v1";

        var openRouter = new DysonModelProviderEntity
        {
            DisplayName = "OpenRouter",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            ManagedSource = DysonManagedSources.OpenRouter,
            ApiKey = apiKey,
            BaseUrl = baseUrl,
        };
        var provider = new OpenAiCompatibleAgentProvider(openRouter, slug: null);
        Assert.Equal(apiKey, provider.ApiKey);
        Assert.Equal(baseUrl, provider.BaseUrl);
        Assert.NotEqual(DysonCliProxyHost.DefaultApiKey, provider.ApiKey);
        Assert.NotEqual(DysonCliProxyHost.DefaultLocalBaseUrl, provider.BaseUrl);
    }
}

public class DysonManagedSourcesTests
{
    [Theory]
    [InlineData("cliproxy-codex", true)]
    [InlineData("cliproxy-grok", true)]
    [InlineData("openrouter", false)]
    [InlineData("orcarouter", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsCliProxy_prefix_gated(string? source, bool expected)
    {
        Assert.Equal(expected, DysonManagedSources.IsCliProxy(source));
    }

    [Theory]
    [InlineData("openrouter", true)]
    [InlineData("orcarouter", false)]
    [InlineData("cliproxy-codex", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsOpenRouter_matches_const(string? source, bool expected)
    {
        Assert.Equal(expected, DysonManagedSources.IsOpenRouter(source));
    }

    [Theory]
    [InlineData("orcarouter", true)]
    [InlineData("openrouter", false)]
    [InlineData("cliproxy-codex", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsOrcaRouter_matches_const(string? source, bool expected)
    {
        Assert.Equal(expected, DysonManagedSources.IsOrcaRouter(source));
    }

    [Theory]
    [InlineData("openrouter", true)]
    [InlineData("orcarouter", true)]
    [InlineData("cliproxy-codex", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsDirectManaged_matches_openrouter_or_orcarouter(string? source, bool expected)
    {
        Assert.Equal(expected, DysonManagedSources.IsDirectManaged(source));
    }
}

public class DysonCliProxyPathsPruneTests
{
    [Fact]
    public void PruneObsoleteVersionDirectories_deletes_old_pin_keeps_shared_state()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var pin = DysonThirdPartyResources.CliProxyApi.Version;
            var oldDir = Directory.CreateDirectory(Path.Combine(root.FullName, "7.2.102"));
            File.WriteAllText(Path.Combine(oldDir.FullName, "stale.bin"), "old");
            var pinDir = Directory.CreateDirectory(Path.Combine(root.FullName, pin));
            File.WriteAllText(Path.Combine(pinDir.FullName, DysonCliProxyPaths.ExecutableFileName), "pin");
            var auths = Directory.CreateDirectory(Path.Combine(root.FullName, DysonCliProxyPaths.AuthsDirectoryName));
            File.WriteAllText(Path.Combine(auths.FullName, "codex-oauth.json"), "{}");
            var config = Path.Combine(root.FullName, DysonCliProxyPaths.ConfigFileName);
            var keys = Path.Combine(root.FullName, DysonCliProxyPaths.KeysFileName);
            File.WriteAllText(config, "host: \"127.0.0.1\"");
            File.WriteAllText(keys, "{}");
            Directory.CreateDirectory(Path.Combine(root.FullName, "not-a-version"));

            DysonCliProxyPaths.PruneObsoleteVersionDirectories(root.FullName, pin);

            Assert.False(Directory.Exists(oldDir.FullName));
            Assert.True(Directory.Exists(pinDir.FullName));
            Assert.True(File.Exists(Path.Combine(pinDir.FullName, DysonCliProxyPaths.ExecutableFileName)));
            Assert.True(Directory.Exists(auths.FullName));
            Assert.True(File.Exists(Path.Combine(auths.FullName, "codex-oauth.json")));
            Assert.True(File.Exists(config));
            Assert.True(File.Exists(keys));
            Assert.True(Directory.Exists(Path.Combine(root.FullName, "not-a-version")));
        }
        finally
        {
            try { root.Delete(recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }
}

public class DysonCliProxyDownloaderForceTests
{
    [Fact]
    public async Task EnsureInstalledAsync_force_true_deletes_pin_dir_without_network()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var pin = DysonThirdPartyResources.CliProxyApi.Version;
            var pinDir = Directory.CreateDirectory(Path.Combine(root.FullName, pin));
            File.WriteAllText(Path.Combine(pinDir.FullName, DysonCliProxyPaths.ExecutableFileName), "old-binary");
            var auths = Directory.CreateDirectory(Path.Combine(root.FullName, DysonCliProxyPaths.AuthsDirectoryName));
            File.WriteAllText(Path.Combine(auths.FullName, "codex-oauth.json"), "{}");
            File.WriteAllText(Path.Combine(root.FullName, DysonCliProxyPaths.ConfigFileName), "keep-config");
            File.WriteAllText(Path.Combine(root.FullName, DysonCliProxyPaths.KeysFileName), "{}");

            using var http = new HttpClient(new FailImmediatelyHandler());
            var downloader = new DysonCliProxyDownloader(http);
            var result = await downloader.EnsureInstalledAsync(root.FullName, force: true);

            Assert.True(result.IsError);
            Assert.False(Directory.Exists(pinDir.FullName));
            Assert.True(Directory.Exists(auths.FullName));
            Assert.True(File.Exists(Path.Combine(auths.FullName, "codex-oauth.json")));
            Assert.Equal("keep-config", File.ReadAllText(Path.Combine(root.FullName, DysonCliProxyPaths.ConfigFileName)));
            Assert.True(File.Exists(Path.Combine(root.FullName, DysonCliProxyPaths.KeysFileName)));
        }
        finally
        {
            try { root.Delete(recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    private sealed class FailImmediatelyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("no network"),
            });
    }
}
