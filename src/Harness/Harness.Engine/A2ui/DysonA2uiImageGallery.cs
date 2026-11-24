using System.Text.Json.Nodes;

namespace DysonHarness;

public enum DysonA2uiGalleryKind
{
    Url,
    Path,
    Invalid,
}

/// <summary>
/// One normalised gallery slide, in input order. <see cref="Value"/> is the url or work-relative path; for
/// <see cref="DysonA2uiGalleryKind.Invalid"/> it is the text the slide shows instead. <see cref="Title"/> is already
/// <see cref="DysonA2uiImage.NormalizeTitle"/>d (the band text).
/// </summary>
public sealed record DysonA2uiGalleryItem(
    int Index,
    DysonA2uiGalleryKind Kind,
    string Value,
    string? Alt = null,
    string? Caption = null,
    string? Title = null,
    string? Description = null)
{
    /// <summary>Alt, description, title, then the file name or url host.</summary>
    public string? Name => DysonA2uiImage.AccessibleName(
        Alt,
        Description,
        null,
        Title,
        Kind switch
        {
            DysonA2uiGalleryKind.Url => DysonA2uiLinks.Host(Value),
            DysonA2uiGalleryKind.Path => System.IO.Path.GetFileName(Value),
            _ => null,
        });
}

/// <summary>
/// Dyson <c>ImageGallery</c>: <c>images</c> (alias <c>items</c>), at most <see cref="MaxImages"/> items, each a string
/// (<c>http(s)://</c> is a url, anything else a work-relative path) or an object with exactly one of <c>url</c> / <c>path</c>
/// plus <c>alt</c>, <c>caption</c>, <c>title</c>, <c>description</c>. Pure rules for the validator, the executor path check
/// and the renderer; nothing here throws.
/// </summary>
public static class DysonA2uiImageGallery
{
    public const string ComponentName = "ImageGallery";
    public const int MaxImages = 50;
    public const int DefaultHeight = 320;
    private const int MinHeight = 120;
    private const int MaxHeight = 800;

    public static int ClampHeight(int? height) => Math.Clamp(height ?? DefaultHeight, MinHeight, MaxHeight);

    /// <summary>
    /// Rules the catalog cannot express (exactly one of url / path per object, url or path per string, at most
    /// <see cref="MaxImages"/>). Returns the pointer suffix under the component and the message, or null. A bound list
    /// (not an array) is checked at render time.
    /// </summary>
    public static (string Suffix, string Message)? Check(JsonNode? images, string key = "images")
    {
        if (images is not JsonArray list)
            return null;
        var at = "/" + key;
        if (list.Count > MaxImages)
            return ($"{at}/{MaxImages}", $"at most {MaxImages} images.");
        for (var i = 0; i < list.Count; i++)
        {
            var pointer = $"{at}/{i}";
            switch (list[i])
            {
                case JsonObject o:
                    var hasUrl = o.ContainsKey("url");
                    var hasPath = o.ContainsKey("path");
                    if (hasUrl == hasPath)
                        return (pointer, "an image needs exactly one of url or path.");
                    if (hasUrl && !DysonA2uiLinks.IsMediaUrl(DysonA2uiValidator.AsString(o["url"])))
                        return ($"{pointer}/url", DysonA2uiLinks.MediaUrlMessage);
                    if (hasPath && KindOf(DysonA2uiValidator.AsString(o["path"])?.Trim()) != DysonA2uiGalleryKind.Path)
                        return ($"{pointer}/path", "path must be a non-blank work-relative file path.");
                    break;
                case JsonValue v when DysonA2uiValidator.AsString(v)?.Trim() is { } s:
                    var kind = KindOf(s);
                    if (kind == DysonA2uiGalleryKind.Invalid)
                        return (pointer, "an image string must be an http(s) URL or a work-relative file path.");
                    if (kind == DysonA2uiGalleryKind.Url && !DysonA2uiLinks.IsMediaUrl(s))
                        return (pointer, DysonA2uiLinks.MediaUrlMessage);
                    break;
                default:
                    return (pointer, "expected a string or an object.");
            }
        }

        return null;
    }

    /// <summary>Resolved <c>images</c> / <c>items</c> value to slides, in input order, at most <see cref="MaxImages"/>. Bad items become Invalid slides with text.</summary>
    public static IReadOnlyList<DysonA2uiGalleryItem> Normalize(JsonNode? resolved)
    {
        if (resolved is not JsonArray list)
            return [];
        var items = new List<DysonA2uiGalleryItem>();
        for (var i = 0; i < list.Count && i < MaxImages; i++)
            items.Add(Item(i, list[i]));
        return items;
    }

    private static DysonA2uiGalleryItem Item(int index, JsonNode? node)
    {
        if (node is JsonObject o)
        {
            var alt = Text(o["alt"]);
            var caption = Text(o["caption"]);
            var title = DysonA2uiImage.NormalizeTitle(DysonA2uiValidator.AsString(o["title"]));
            var description = Text(o["description"]);
            var url = o["url"];
            var path = o["path"];
            if ((url is null) == (path is null))
                return new(index, DysonA2uiGalleryKind.Invalid, "Needs exactly one of url or path.", alt, caption, title, description);
            var value = Text(url ?? path);
            var kind = url is not null
                ? (value is not null && KindOf(value) == DysonA2uiGalleryKind.Url ? DysonA2uiGalleryKind.Url : DysonA2uiGalleryKind.Invalid)
                : KindOf(value) == DysonA2uiGalleryKind.Path ? DysonA2uiGalleryKind.Path : DysonA2uiGalleryKind.Invalid;
            return kind == DysonA2uiGalleryKind.Invalid
                ? new(index, kind, "Not a valid image address.", alt, caption, title, description)
                : new(index, kind, value!, alt, caption, title, description);
        }

        var text = Text(node);
        return KindOf(text) switch
        {
            DysonA2uiGalleryKind.Url => new(index, DysonA2uiGalleryKind.Url, text!),
            DysonA2uiGalleryKind.Path => new(index, DysonA2uiGalleryKind.Path, text!),
            _ => new(index, DysonA2uiGalleryKind.Invalid, "Not a valid image address."),
        };
    }

    private static string? Text(JsonNode? node) =>
        DysonA2uiValidator.AsString(node)?.Trim() is { Length: > 0 } s ? s : null;

    private static DysonA2uiGalleryKind KindOf(string? s) =>
        string.IsNullOrWhiteSpace(s) ? DysonA2uiGalleryKind.Invalid
        : s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? DysonA2uiGalleryKind.Url
        : HasScheme(s) ? DysonA2uiGalleryKind.Invalid
        : DysonA2uiGalleryKind.Path;

    /// <summary><c>ftp:</c>, <c>data:</c>, <c>file:</c> and the like (two or more scheme chars; <c>C:</c> is a drive, left to the path rules).</summary>
    private static bool HasScheme(string s)
    {
        var colon = s.IndexOf(':');
        if (colon < 2)
            return false;
        for (var i = 0; i < colon; i++)
        {
            if (!char.IsAsciiLetterOrDigit(s[i]) && s[i] is not ('+' or '-' or '.'))
                return false;
        }

        return true;
    }

    /// <summary>Path values of every ImageGallery with their pointer, for the executor's async path check (string items and <c>path</c> objects; urls are skipped).</summary>
    public static IReadOnlyList<(string Pointer, string Path)> PathPointers(IReadOnlyList<DysonA2uiMessage> messages)
    {
        var list = new List<(string, string)>();
        foreach (var message in messages)
        {
            if (message.Body["components"] is not JsonArray components)
                continue;
            for (var k = 0; k < components.Count; k++)
            {
                if (components[k] is not JsonObject c || DysonA2uiValidator.AsString(c["component"]) != ComponentName)
                    continue;
                foreach (var key in new[] { "images", "items" })
                {
                    if (c[key] is not JsonArray images)
                        continue;
                    for (var i = 0; i < images.Count; i++)
                    {
                        var at = $"{message.Path}/components/{k}/{key}/{i}";
                        if (images[i] is JsonObject o)
                        {
                            if (o["url"] is null && DysonA2uiValidator.AsString(o["path"])?.Trim() is { } p && KindOf(p) == DysonA2uiGalleryKind.Path)
                                list.Add(($"{at}/path", p));
                        }
                        else if (DysonA2uiValidator.AsString(images[i])?.Trim() is { } s && KindOf(s) == DysonA2uiGalleryKind.Path)
                        {
                            list.Add((at, s));
                        }
                    }
                }
            }
        }

        return list;
    }

    /// <summary>Distinct hosts, in order, of the url slides that may load (for "Load N remote images (a.com, +1 host)").</summary>
    public static IReadOnlyList<string> RemoteHosts(IReadOnlyList<DysonA2uiGalleryItem> items) =>
        items.Where(i => i.Kind == DysonA2uiGalleryKind.Url && IsLoadableUrl(i.Value))
            .Select(i => DysonA2uiLinks.Host(i.Value))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The AudioPlayer host rule (no localhost, single-label, private suffixes, private IP literals, credentials).</summary>
    public static bool IsLoadableUrl(string? url) =>
        DysonA2uiAudio.PlayableUrl("open_url:" + url) is not null;

    /// <summary>Next slide index: clamped inline (<paramref name="wrap"/> false), wrapped in the modal. Count 0 gives 0.</summary>
    public static int Step(int index, int delta, int count, bool wrap)
    {
        if (count <= 0)
            return 0;
        var next = index + delta;
        return wrap ? ((next % count) + count) % count : Math.Clamp(next, 0, count - 1);
    }
}
