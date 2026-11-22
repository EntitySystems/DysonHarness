using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// What a LocalVideo card shows and opens. <see cref="AbsolutePath"/> is already resolved inside the
/// work directory; <see cref="ContentType"/> comes from <see cref="DysonVideoFiles"/>.
/// </summary>
public sealed record DysonA2uiLocalVideoCard(string Title, string RelativePath, string AbsolutePath, string ContentType)
{
    /// <summary>The built-in func the card click dispatches: the same file viewer a video file opens in.</summary>
    public string OpenFunc => DysonBuiltInConversationActions.OpenFilePrefix + RelativePath;
}

/// <summary>
/// Dyson-catalog <c>LocalVideo</c> (<c>path</c>, optional <c>title</c>): a card for a video file in the
/// work directory. Paths use the <c>open_file:</c> rules (no rooted path, no <c>..</c>, inside the work root);
/// an escaping path fails RenderA2ui, while a missing file or an unsupported extension is card text.
/// Supported formats are <see cref="DysonVideoFiles"/> (the file viewer's list).
/// </summary>
public static class DysonA2uiLocalVideo
{
    public const string ComponentName = "LocalVideo";

    /// <summary>LocalVideo <c>path</c> values with their pointer, for the executor's async path check.</summary>
    public static IReadOnlyList<(string Pointer, string Path)> PathPointers(IReadOnlyList<DysonA2uiMessage> messages)
    {
        var list = new List<(string, string)>();
        foreach (var message in messages)
        {
            if (message.Body["components"] is not JsonArray components)
                continue;
            for (var k = 0; k < components.Count; k++)
            {
                if (components[k] is JsonObject c
                    && c["component"] is JsonValue name && name.TryGetValue<string>(out var n) && n == ComponentName
                    && c["path"] is JsonValue p && p.TryGetValue<string>(out var path))
                {
                    list.Add(($"{message.Path}/components/{k}/path", path));
                }
            }
        }

        return list;
    }

    /// <summary>Work-relative path, validated exactly as <c>open_file:</c> validates it.</summary>
    public static async Task<Result<string, string>> ResolveRelativeAsync(
        string? path,
        string? workRoot,
        CancellationToken cancellationToken = default)
    {
        var resolved = await DysonBuiltInConversationActions
            .TryResolveAsync(DysonBuiltInConversationActions.OpenFilePrefix + (path ?? ""), workRoot, cancellationToken)
            .ConfigureAwait(false);
        return resolved switch
        {
            null => Result<string, string>.AsError("File path is required."),
            { IsError: true } failed => Result<string, string>.AsError(failed.Error),
            { } ok => Result<string, string>.AsValue(ok.Value.PathOrUrl ?? ""),
        };
    }

    /// <summary>
    /// Card data, or the text the card shows instead (escaping path, unsupported extension, missing file).
    /// Never throws for bad input. Title defaults to the file name.
    /// </summary>
    public static async Task<Result<DysonA2uiLocalVideoCard, string>> LoadCardAsync(
        string? path,
        string? title,
        string? workRoot,
        CancellationToken cancellationToken = default)
    {
        var relative = await ResolveRelativeAsync(path, workRoot, cancellationToken).ConfigureAwait(false);
        if (relative.IsError)
            return Result<DysonA2uiLocalVideoCard, string>.AsError(relative.Error);

        var rel = relative.Value;
        var contentType = DysonVideoFiles.ContentTypeFor(rel);
        if (contentType is null)
            return Result<DysonA2uiLocalVideoCard, string>.AsError($"Not a supported video file: {rel}");

        var created = await DysonWorkspaceFileSystems.CreateLocalAsync(workRoot!, cancellationToken).ConfigureAwait(false);
        if (created.IsError)
            return Result<DysonA2uiLocalVideoCard, string>.AsError(created.Error);

        var fs = created.Value;
        var absolute = fs.ResolvePath(rel);
        if (absolute.IsError)
            return Result<DysonA2uiLocalVideoCard, string>.AsError(absolute.Error);

        var exists = await fs.FileExistsAsync(rel, cancellationToken).ConfigureAwait(false);
        if (exists.IsError)
            return Result<DysonA2uiLocalVideoCard, string>.AsError(exists.Error);
        if (!exists.Value)
            return Result<DysonA2uiLocalVideoCard, string>.AsError($"File not found: {rel}");

        var shown = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(rel) : title.Trim();
        return Result<DysonA2uiLocalVideoCard, string>.AsValue(
            new DysonA2uiLocalVideoCard(shown, rel, absolute.Value, contentType));
    }
}
