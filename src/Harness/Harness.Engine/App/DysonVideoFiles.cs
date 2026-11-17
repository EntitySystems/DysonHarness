using System.Collections.Frozen;

namespace DysonHarness;

/// <summary>
/// Known video file extensions and their MIME types, for in-app playback (file viewer video player).
/// Matching is on the extension only, case-insensitive. No .ogv: neither WebView2 nor Safari plays Theora.
/// </summary>
public static class DysonVideoFiles
{
    private static readonly FrozenDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".mp4"] = "video/mp4",
            [".m4v"] = "video/mp4",
            [".webm"] = "video/webm",
            [".mov"] = "video/quicktime",
            [".mkv"] = "video/x-matroska",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Video MIME type for <paramref name="path"/>'s extension, or null when it is not a known video file.</summary>
    public static string? ContentTypeFor(string? path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && ContentTypes.TryGetValue(ext, out var contentType)
            ? contentType
            : null;
    }

    /// <summary>True when <paramref name="path"/> has a known video extension.</summary>
    public static bool IsVideoPath(string? path) => ContentTypeFor(path) is not null;
}
