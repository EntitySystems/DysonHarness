using System.Collections.Frozen;

namespace DysonHarness;

/// <summary>
/// Known audio file extensions and their MIME types, for in-app playback (file viewer audio player).
/// Matching is on the extension only, case-insensitive.
/// </summary>
public static class DysonAudioFiles
{
    private static readonly FrozenDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".mp3"] = "audio/mpeg",
            [".wav"] = "audio/wav",
            [".ogg"] = "audio/ogg",
            [".oga"] = "audio/ogg",
            [".opus"] = "audio/ogg",
            [".flac"] = "audio/flac",
            [".m4a"] = "audio/mp4",
            [".aac"] = "audio/aac",
            [".weba"] = "audio/webm",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Audio MIME type for <paramref name="path"/>'s extension, or null when it is not a known audio file.</summary>
    public static string? ContentTypeFor(string? path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && ContentTypes.TryGetValue(ext, out var contentType)
            ? contentType
            : null;
    }

    /// <summary>True when <paramref name="path"/> has a known audio extension.</summary>
    public static bool IsAudioPath(string? path) => ContentTypeFor(path) is not null;
}
