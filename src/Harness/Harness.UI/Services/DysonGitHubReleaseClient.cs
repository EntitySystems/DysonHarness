using System.Net.Http.Headers;
using System.Text.Json;
using DysonHarness;

namespace Harness.UI.Services;

/// <summary>A GitHub release that ships a Windows MSI asset.</summary>
public sealed record DysonGitHubMsiRelease(
    string TagName,
    System.Version Version,
    string AssetName,
    string DownloadUrl,
    long SizeBytes,
    Uri ReleasePageUrl);

/// <summary>
/// Lists GitHub releases for the app repo and picks the newest one on the local
/// release track (<c>stable</c> = non-prerelease, <c>preview</c> = prerelease)
/// carrying a <c>*-win-x64.msi</c> asset.
/// </summary>
public sealed class DysonGitHubReleaseClient(HttpClient http)
{
    public const string HttpClientName = "github-releases";
    public const string MsiAssetSuffix = "-win-x64.msi";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>Newest non-draft release on <paramref name="channel"/> with an MSI asset; success with a null value when none qualifies.</summary>
    public async Task<Result<DysonGitHubMsiRelease?, string>> FindNewestMsiReleaseAsync(
        string repo,
        string channel = DysonAppVersionInfo.ChannelPreview,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{repo}/releases?per_page=15";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DysonHarness", "1.0"));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result<DysonGitHubMsiRelease?, string>.AsError($"GitHub releases HTTP {(int)response.StatusCode}.");

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Result<DysonGitHubMsiRelease?, string>.AsValue(SelectNewestMsiRelease(json, channel));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<DysonGitHubMsiRelease?, string>.AsError("Update check was cancelled.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Result<DysonGitHubMsiRelease?, string>.AsError($"Update check failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Picks the highest CalVer non-draft release with an MSI asset whose
    /// <c>prerelease</c> flag matches <paramref name="channel"/>
    /// (<c>preview</c> → prerelease, <c>stable</c> → full release).
    /// </summary>
    public static DysonGitHubMsiRelease? SelectNewestMsiRelease(
        string releasesJson,
        string channel = DysonAppVersionInfo.ChannelPreview)
    {
        if (string.IsNullOrWhiteSpace(releasesJson))
            return null;

        var wantPrerelease = DysonAppVersionInfo.NormalizeChannel(channel) == DysonAppVersionInfo.ChannelPreview;

        try
        {
            using var document = JsonDocument.Parse(releasesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            DysonGitHubMsiRelease? best = null;
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object)
                    continue;
                if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                    continue;

                var isPrerelease = release.TryGetProperty("prerelease", out var prerelease)
                    && prerelease.ValueKind == JsonValueKind.True;
                if (isPrerelease != wantPrerelease)
                    continue;

                var tag = release.TryGetProperty("tag_name", out var tagName) ? tagName.GetString() : null;
                var version = DysonAppVersionInfo.ParseCalVer(tag);
                if (version is null || (best is not null && version <= best.Version))
                    continue;

                if (FindMsiAsset(release) is not { } asset || FindReleasePageUrl(release, tag!) is not { } releasePageUrl)
                    continue;

                best = new DysonGitHubMsiRelease(tag!, version, asset.Name, asset.Url, asset.SizeBytes, releasePageUrl);
            }

            return best;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Uri? FindReleasePageUrl(JsonElement release, string tag)
    {
        var htmlUrl = release.TryGetProperty("html_url", out var htmlUrlElement) ? htmlUrlElement.GetString() : null;
        if (!Uri.TryCreate(htmlUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is [_, _, "releases", "tag", var pageTag]
               && string.Equals(Uri.UnescapeDataString(pageTag), tag, StringComparison.Ordinal)
            ? uri
            : null;
    }

    private static (string Name, string Url, long SizeBytes)? FindMsiAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (name is null || !name.EndsWith(MsiAssetSuffix, StringComparison.OrdinalIgnoreCase))
                continue;

            var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var bytes)
                ? bytes
                : 0;
            return (name, url, size);
        }

        return null;
    }
}
