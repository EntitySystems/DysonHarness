using System.Net;
using System.Net.Sockets;

namespace DysonHarness;

/// <summary>
/// What an <c>AudioPlayer.path</c> card plays. <see cref="AbsolutePath"/> is already resolved inside the work
/// directory; <see cref="ContentType"/> comes from <see cref="DysonA2uiAudio.ContentTypeFor"/>.
/// </summary>
public sealed record DysonA2uiAudioCard(string Title, string RelativePath, string AbsolutePath, string ContentType)
{
    /// <summary>The built-in func the fallback "Open in file viewer" button dispatches.</summary>
    public string OpenFunc => DysonBuiltInConversationActions.OpenFilePrefix + RelativePath;
}

/// <summary>
/// Kit <c>AudioPlayer</c> with the Dyson <c>path</c> extension (exactly one of <c>url</c> / <c>path</c>): an audio file in
/// the work directory (streamed with Range), or a remote http(s) <c>url</c> that loads only after the user presses play.
/// Paths use the <c>open_file:</c> rules through <see cref="DysonA2uiLocalVideo.ResolveRelativeAsync"/>; an escaping path
/// fails RenderA2ui, while a missing, unsupported or over-cap file is card text.
/// </summary>
public static class DysonA2uiAudio
{
    public const string ComponentName = "AudioPlayer";

    // ponytail: streamed with Range, never buffered; the cap is only a sanity bound.
    public const long MaxBytes = 500L * 1024 * 1024;

    /// <summary>Audio MIME type for mp3, m4a, aac, wav, ogg/oga/opus, flac, weba; null for everything else.</summary>
    public static string? ContentTypeFor(string path) => DysonAudioFiles.ContentTypeFor(path);

    /// <summary>
    /// Card data, or the text the card shows instead (escaping path, unsupported extension, missing or over-cap file).
    /// Never throws for bad input. Title defaults to the file name.
    /// </summary>
    public static async Task<Result<DysonA2uiAudioCard, string>> LoadCardAsync(
        string? path,
        string? title,
        string? workRoot,
        CancellationToken cancellationToken = default)
    {
        var relative = await DysonA2uiLocalVideo.ResolveRelativeAsync(path, workRoot, cancellationToken).ConfigureAwait(false);
        if (relative.IsError)
            return Result<DysonA2uiAudioCard, string>.AsError(relative.Error);

        var rel = relative.Value;
        var contentType = ContentTypeFor(rel);
        if (contentType is null)
            return Result<DysonA2uiAudioCard, string>.AsError($"Not a supported audio file: {rel} (mp3, m4a, aac, wav, ogg, opus, flac, weba)");

        var created = await DysonWorkspaceFileSystems.CreateLocalAsync(workRoot!, cancellationToken).ConfigureAwait(false);
        if (created.IsError)
            return Result<DysonA2uiAudioCard, string>.AsError(created.Error);

        var fs = created.Value;
        var absolute = fs.ResolvePath(rel);
        if (absolute.IsError)
            return Result<DysonA2uiAudioCard, string>.AsError(absolute.Error);

        var exists = await fs.FileExistsAsync(rel, cancellationToken).ConfigureAwait(false);
        if (exists.IsError)
            return Result<DysonA2uiAudioCard, string>.AsError(exists.Error);
        if (!exists.Value)
            return Result<DysonA2uiAudioCard, string>.AsError($"File not found: {rel}");

        var length = await fs.GetFileLengthAsync(rel, cancellationToken).ConfigureAwait(false);
        if (length.IsError)
            return Result<DysonA2uiAudioCard, string>.AsError(length.Error);
        if (length.Value > MaxBytes)
            return Result<DysonA2uiAudioCard, string>.AsError($"Audio file is larger than 500 MB: {rel}");

        var shown = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(rel) : title.Trim();
        return Result<DysonA2uiAudioCard, string>.AsValue(
            new DysonA2uiAudioCard(shown, rel, absolute.Value, contentType));
    }

    /// <summary>
    /// The http(s) URL a click-to-load player may fetch: <paramref name="linkFunc"/> must be <c>open_url:</c> plus an
    /// absolute http/https URI whose host is not <c>localhost</c> nor a loopback, private, link-local or unspecified IP
    /// literal. Otherwise null (the UI keeps the link card). ponytail: literal-host check only; DNS rebinding and public
    /// names that resolve to the LAN are not covered (the user click and no-cors media fetch are the mitigation).
    /// </summary>
    public static string? PlayableUrl(string? linkFunc)
    {
        const string prefix = "open_url:";
        var s = linkFunc?.Trim();
        if (s is null || !s.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var url = s[prefix.Length..].Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Host.Length == 0
            || uri.UserInfo.Length > 0)
            return null;

        // A trailing dot (localhost., 127.0.0.1.) and non-ASCII dots (U+3002) reach the browser as the same host.
        var host = uri.DnsSafeHost.TrimEnd('.');
        if (host.Length == 0 || host.Any(c => c > 127))
            return null;
        if (IPAddress.TryParse(host, out var ip))
            return IsLocalAddress(ip) ? null : url;

        // Intranet names: localhost, single-label hosts (http://nas/) and well-known private suffixes.
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.')
            || PrivateSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            ? null
            : url;
    }

    private static readonly string[] PrivateSuffixes = [".localhost", ".local", ".internal", ".lan", ".home.arpa"];

    private static bool IsLocalAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.ScopeId != 0
                || ip.GetAddressBytes().Take(12).All(x => x == 0); // ::a.b.c.d (IPv4-compatible)
        var b = ip.GetAddressBytes();
        return b[0] == 0
            || b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] is >= 64 and <= 127) // CGNAT
            || (b[0] == 198 && b[1] is 18 or 19)
            || b[0] >= 224; // multicast, reserved, broadcast
    }
}
