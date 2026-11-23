namespace DysonHarness;

/// <summary>
/// What an <c>Image.path</c> card shows and opens. <see cref="AbsolutePath"/> is already resolved inside the work
/// directory; <see cref="ContentType"/> comes from <see cref="DysonA2uiImage.ContentTypeFor"/>.
/// </summary>
public sealed record DysonA2uiImageCard(string Title, string RelativePath, string AbsolutePath, string ContentType)
{
    /// <summary>The built-in func the modal's "Open in file viewer" dispatches (the viewer has zoom).</summary>
    public string OpenFunc => DysonBuiltInConversationActions.OpenFilePrefix + RelativePath;
}

/// <summary>
/// Kit <c>Image</c> with the Dyson <c>path</c> extension (exactly one of <c>url</c> / <c>path</c>): a png, jpg, gif, webp or
/// bmp file in the work directory, shown inline and expandable to a modal. Paths use the <c>open_file:</c> rules through
/// <see cref="DysonA2uiLocalVideo.ResolveRelativeAsync"/>; an escaping path fails RenderA2ui, while a missing,
/// unsupported or over-cap file is card text. Remote <c>url</c> images are never fetched (placeholder).
/// </summary>
public static class DysonA2uiImage
{
    public const string ComponentName = "Image";

    /// <summary>Default inline box height in px for a <c>variant</c> (<c>height</c> overrides it).</summary>
    public static int DefaultHeight(string? variant) => variant switch
    {
        "icon" => 32,
        "avatar" => 48,
        "small" => 120,
        "header" => 200,
        "large" => 400,
        _ => 240,
    };

    // ponytail: 20 MB cap; the browser decodes the full image. Upgrade: server-side downscale (ConvertImage/Magick) for big files.
    public const long MaxBytes = 20L * 1024 * 1024;

    /// <summary>
    /// <c>image/*</c> for png, jpg/jpeg, gif, webp, bmp; null for everything else (no SVG: script risk; no TIFF/ICO:
    /// browsers do not render them).
    /// </summary>
    public static string? ContentTypeFor(string path) =>
        DysonComposerUploads.ImageContentTypeFromFileName(path) is var type
        && type is "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp"
            ? type
            : null;

    /// <summary>
    /// Card data, or the text the card shows instead (escaping path, unsupported extension, missing or over-cap file).
    /// Never throws for bad input. Title defaults to the file name.
    /// </summary>
    public static async Task<Result<DysonA2uiImageCard, string>> LoadCardAsync(
        string? path,
        string? title,
        string? workRoot,
        CancellationToken cancellationToken = default)
    {
        var relative = await DysonA2uiLocalVideo.ResolveRelativeAsync(path, workRoot, cancellationToken).ConfigureAwait(false);
        if (relative.IsError)
            return Result<DysonA2uiImageCard, string>.AsError(relative.Error);

        var rel = relative.Value;
        var contentType = ContentTypeFor(rel);
        if (contentType is null)
            return Result<DysonA2uiImageCard, string>.AsError($"Not a supported image file: {rel} (png, jpg, gif, webp, bmp)");

        var created = await DysonWorkspaceFileSystems.CreateLocalAsync(workRoot!, cancellationToken).ConfigureAwait(false);
        if (created.IsError)
            return Result<DysonA2uiImageCard, string>.AsError(created.Error);

        var fs = created.Value;
        var absolute = fs.ResolvePath(rel);
        if (absolute.IsError)
            return Result<DysonA2uiImageCard, string>.AsError(absolute.Error);

        var exists = await fs.FileExistsAsync(rel, cancellationToken).ConfigureAwait(false);
        if (exists.IsError)
            return Result<DysonA2uiImageCard, string>.AsError(exists.Error);
        if (!exists.Value)
            return Result<DysonA2uiImageCard, string>.AsError($"File not found: {rel}");

        var length = await fs.GetFileLengthAsync(rel, cancellationToken).ConfigureAwait(false);
        if (length.IsError)
            return Result<DysonA2uiImageCard, string>.AsError(length.Error);
        if (length.Value > MaxBytes)
            return Result<DysonA2uiImageCard, string>.AsError($"Image is larger than 20 MB: {rel}");

        var shown = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(rel) : title.Trim();
        return Result<DysonA2uiImageCard, string>.AsValue(
            new DysonA2uiImageCard(shown, rel, absolute.Value, contentType));
    }
}
