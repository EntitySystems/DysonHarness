using System.Text;
using System.Text.Json;

namespace DysonHarness;

public sealed partial class DysonWorkspaceToolExecutor
{
    private const int HtmlVisualizationMaxAssetBytes = 256 * 1024;
    private const int HtmlVisualizationMaxTotalBytes = 512 * 1024;
    private const int HtmlVisualizationMaxPerSession = 20;

    private Task<DysonToolCallResult> RenderHtmlVisualizationAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var document = JsonDocument.Parse(ArgsOrEmpty(call));
            var root = document.RootElement;
            var titleResult = RequireString(root, "title");
            if (titleResult.IsError)
                return Task.FromResult(Error(call, "RenderHtmlVisualization: " + titleResult.Error));

            var title = titleResult.Value.Trim();
            if (title.Length > 120)
                return Task.FromResult(Error(call, "RenderHtmlVisualization: title must be at most 120 characters."));

            var html = ResolveVisualizationAsset(root, "html", [".html", ".htm"], allowEmpty: false);
            if (html.IsError)
                return Task.FromResult(Error(call, html.Error));
            var css = ResolveVisualizationAsset(root, "css", [".css"], allowEmpty: true);
            if (css.IsError)
                return Task.FromResult(Error(call, css.Error));
            var javascript = ResolveVisualizationAsset(root, "js", [".js", ".mjs"], allowEmpty: true);
            if (javascript.IsError)
                return Task.FromResult(Error(call, javascript.Error));

            var totalBytes = Encoding.UTF8.GetByteCount(html.Value)
                + Encoding.UTF8.GetByteCount(css.Value)
                + Encoding.UTF8.GetByteCount(javascript.Value);
            if (totalBytes > HtmlVisualizationMaxTotalBytes)
            {
                return Task.FromResult(Error(
                    call,
                    "RenderHtmlVisualization: total resolved source exceeds the 512 KiB UTF-8 limit."));
            }

            var visualizationCount = _session.Turns
                .SelectMany(turn => turn.TrackedToolCalls)
                .Count(tracked => tracked.Result is { IsError: false, HtmlVisualization: not null });
            if (visualizationCount >= HtmlVisualizationMaxPerSession)
            {
                return Task.FromResult(Error(
                    call,
                    "RenderHtmlVisualization: this session already has the maximum of 20 successful visualizations."));
            }

            var visualization = new DysonHtmlVisualization
            {
                Id = Guid.NewGuid(),
                Title = title,
                Html = html.Value,
                Css = css.Value,
                JavaScript = javascript.Value,
            };
            var acknowledgement = JsonSerializer.Serialize(new
            {
                visualizationId = visualization.Id,
                title = visualization.Title,
                rendered = true,
            });
            return Task.FromResult(Ok(call, acknowledgement, htmlVisualization: visualization));
        }
        catch (JsonException)
        {
            return Task.FromResult(Error(call, "RenderHtmlVisualization: invalid JSON arguments."));
        }
    }

    private Result<string, string> ResolveVisualizationAsset(
        JsonElement root,
        string propertyName,
        IReadOnlyCollection<string> permittedExtensions,
        bool allowEmpty)
    {
        if (!root.TryGetProperty(propertyName, out var asset) || asset.ValueKind != JsonValueKind.Object)
            return Result<string, string>.AsError($"RenderHtmlVisualization: '{propertyName}' must be an object.");

        var hasContent = asset.TryGetProperty("content", out var content);
        var hasTempFile = asset.TryGetProperty("tempFile", out var tempFile);
        if (hasContent == hasTempFile)
        {
            return Result<string, string>.AsError(
                $"RenderHtmlVisualization: '{propertyName}' must provide exactly one of content or tempFile.");
        }

        if (hasContent)
        {
            if (content.ValueKind != JsonValueKind.String)
            {
                return Result<string, string>.AsError(
                    $"RenderHtmlVisualization: '{propertyName}.content' must be a string.");
            }

            var raw = content.GetString() ?? "";
            if (!allowEmpty && string.IsNullOrWhiteSpace(raw))
            {
                return Result<string, string>.AsError(
                    $"RenderHtmlVisualization: '{propertyName}.content' must be non-empty.");
            }

            return ValidateVisualizationAssetSize(propertyName, raw);
        }

        if (tempFile.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tempFile.GetString()))
        {
            return Result<string, string>.AsError(
                $"RenderHtmlVisualization: '{propertyName}.tempFile' must be a non-empty string.");
        }

        var path = tempFile.GetString()!;
        if (!IsGeneratedTemporaryPath(path, permittedExtensions))
        {
            return Result<string, string>.AsError(
                $"RenderHtmlVisualization: '{propertyName}.tempFile' must be an exact matching file under .dyson/temp/.");
        }

        var nativePath = _fs.ResolvePath(path);
        if (nativePath.IsError)
            return Result<string, string>.AsError(nativePath.Error);
        if (HasReparsePoint(nativePath.Value))
        {
            return Result<string, string>.AsError(
                $"RenderHtmlVisualization: '{propertyName}.tempFile' cannot include a symlink or reparse point.");
        }

        var length = _fs.GetFileLength(path);
        if (length.IsError)
            return Result<string, string>.AsError(length.Error);
        if (length.Value > HtmlVisualizationMaxAssetBytes)
        {
            return Result<string, string>.AsError(
                $"RenderHtmlVisualization: '{propertyName}.tempFile' exceeds the 256 KiB UTF-8 limit.");
        }

        var read = _fs.ReadAllText(path);
        if (read.IsError)
            return Result<string, string>.AsError(read.Error);
        if (!allowEmpty && string.IsNullOrWhiteSpace(read.Value))
        {
            return Result<string, string>.AsError(
                $"RenderHtmlVisualization: '{propertyName}.tempFile' must be non-empty.");
        }

        return ValidateVisualizationAssetSize(propertyName, read.Value);
    }

    private static Result<string, string> ValidateVisualizationAssetSize(string propertyName, string source) =>
        Encoding.UTF8.GetByteCount(source) > HtmlVisualizationMaxAssetBytes
            ? Result<string, string>.AsError(
                $"RenderHtmlVisualization: '{propertyName}' exceeds the 256 KiB UTF-8 limit.")
            : Result<string, string>.AsValue(source);

    private static bool IsGeneratedTemporaryPath(string path, IReadOnlyCollection<string> permittedExtensions)
    {
        if (Path.IsPathRooted(path))
            return false;

        if (path.Contains('\\'))
            return false;

        var normalized = path;
        if (!normalized.StartsWith(".dyson/temp/", StringComparison.Ordinal)
            || normalized[".dyson/temp/".Length..].Contains('/'))
        {
            return false;
        }

        var fileName = normalized[".dyson/temp/".Length..];
        var extension = Path.GetExtension(fileName);
        if (!permittedExtensions.Contains(extension))
            return false;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var dash = stem.LastIndexOf('-');
        return dash > 0
            && stem.Length - dash - 1 == 24
            && stem[(dash + 1)..].All(Uri.IsHexDigit);
    }

    private static bool HasReparsePoint(string nativePath)
    {
        try
        {
            var current = Path.GetPathRoot(nativePath);
            if (string.IsNullOrEmpty(current))
                return true;

            var remainder = nativePath[current.Length..];
            foreach (var segment in remainder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (string.IsNullOrEmpty(segment))
                    continue;

                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }

            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
