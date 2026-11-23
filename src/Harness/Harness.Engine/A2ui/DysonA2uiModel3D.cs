namespace DysonHarness;

/// <summary>
/// What a Model3D card loads. <see cref="AbsolutePath"/> is already resolved inside the work directory;
/// <see cref="ContentType"/> comes from <see cref="DysonA2uiModel3D.ContentTypeFor"/>; <see cref="Format"/> is
/// <c>glb</c> or <c>fbx</c> (the loader the viewer picks).
/// </summary>
public sealed record DysonA2uiModel3DCard(string Title, string RelativePath, string AbsolutePath, string ContentType)
{
    public string Format => RelativePath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ? "fbx" : "glb";
}

/// <summary>
/// Dyson-catalog <c>Model3D</c> (<c>path</c>, optional <c>title</c>, <c>height</c> 160..800): a three.js viewer for a
/// <c>.glb</c> or <c>.fbx</c> file in the work directory. Paths use the <c>open_file:</c> rules through
/// <see cref="DysonA2uiLocalVideo.ResolveRelativeAsync"/>; an escaping path fails RenderA2ui, while a missing,
/// unsupported or over-cap file is card text.
/// </summary>
public static class DysonA2uiModel3D
{
    public const string ComponentName = "Model3D";

    /// <summary>Default inline viewport height in px when <c>height</c> is omitted.</summary>
    public const int DefaultHeight = 320;

    // ponytail: the whole file is read into one ArrayBuffer per viewer (inline plus modal). Upgrade path: stream via a
    // range-aware loader, or raise the cap.
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary><c>model/gltf-binary</c> for .glb, <c>application/octet-stream</c> for .fbx (no registered type), else null.</summary>
    public static string? ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".glb" => "model/gltf-binary",
        ".fbx" => "application/octet-stream",
        _ => null,
    };

    /// <summary>
    /// Card data, or the text the card shows instead (escaping path, unsupported extension, missing or over-cap file).
    /// Never throws for bad input. Title defaults to the file name.
    /// </summary>
    public static async Task<Result<DysonA2uiModel3DCard, string>> LoadCardAsync(
        string? path,
        string? title,
        string? workRoot,
        CancellationToken cancellationToken = default)
    {
        var relative = await DysonA2uiLocalVideo.ResolveRelativeAsync(path, workRoot, cancellationToken).ConfigureAwait(false);
        if (relative.IsError)
            return Result<DysonA2uiModel3DCard, string>.AsError(relative.Error);

        var rel = relative.Value;
        var contentType = ContentTypeFor(rel);
        if (contentType is null)
            return Result<DysonA2uiModel3DCard, string>.AsError($"Not a supported 3D model file (.glb or .fbx): {rel}");

        var created = await DysonWorkspaceFileSystems.CreateLocalAsync(workRoot!, cancellationToken).ConfigureAwait(false);
        if (created.IsError)
            return Result<DysonA2uiModel3DCard, string>.AsError(created.Error);

        var fs = created.Value;
        var absolute = fs.ResolvePath(rel);
        if (absolute.IsError)
            return Result<DysonA2uiModel3DCard, string>.AsError(absolute.Error);

        var exists = await fs.FileExistsAsync(rel, cancellationToken).ConfigureAwait(false);
        if (exists.IsError)
            return Result<DysonA2uiModel3DCard, string>.AsError(exists.Error);
        if (!exists.Value)
            return Result<DysonA2uiModel3DCard, string>.AsError($"File not found: {rel}");

        var length = await fs.GetFileLengthAsync(rel, cancellationToken).ConfigureAwait(false);
        if (length.IsError)
            return Result<DysonA2uiModel3DCard, string>.AsError(length.Error);
        if (length.Value > MaxBytes)
            return Result<DysonA2uiModel3DCard, string>.AsError("3D model is larger than 64 MB.");

        var shown = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(rel) : title.Trim();
        return Result<DysonA2uiModel3DCard, string>.AsValue(
            new DysonA2uiModel3DCard(shown, rel, absolute.Value, contentType));
    }
}
