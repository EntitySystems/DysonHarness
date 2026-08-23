using System.Text;

namespace DysonHarness;

/// <summary>
/// Durable, display-safe metadata for a PNG produced by <c>GenerateImage</c>.
/// Image bytes remain in the workspace; transient preview URLs and base64 are never stored here.
/// </summary>
public sealed class DysonGeneratedImageArtifact
{
    /// <summary>Workspace directory reserved for generated image artifacts.</summary>
    public const string RelativeDirectory = ".dyson/image-gen";

    /// <summary>Normalized, workspace-relative PNG path beneath <see cref="RelativeDirectory"/>.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Leaf filename matching <see cref="RelativePath"/>.</summary>
    public required string FileName { get; init; }

    /// <summary>Always <c>image/png</c> for durable generated-image artifacts.</summary>
    public required string MimeType { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int ByteLength { get; init; }

    /// <summary>Configured model's user-facing display label.</summary>
    public required string ModelLabel { get; init; }

    /// <summary>Configured model's stable slug.</summary>
    public required string ModelSlug { get; init; }

    /// <summary>
    /// Validates and creates a durable artifact. Paths are accepted only as normalized relative
    /// PNG files beneath <c>.dyson/image-gen</c>; callers must not use persisted metadata as an
    /// arbitrary filesystem path.
    /// </summary>
    public static Result<DysonGeneratedImageArtifact, string> TryCreate(
        string? relativePath,
        string? fileName,
        string? mimeType,
        int width,
        int height,
        int byteLength,
        string? modelLabel,
        string? modelSlug)
    {
        var safePath = ValidateRelativePath(relativePath);
        if (safePath.IsError)
            return Result<DysonGeneratedImageArtifact, string>.AsError(safePath.Error);

        var safeFileName = ValidateFileName(fileName, safePath.Value);
        if (safeFileName.IsError)
            return Result<DysonGeneratedImageArtifact, string>.AsError(safeFileName.Error);

        if (!string.Equals(mimeType, "image/png", StringComparison.OrdinalIgnoreCase))
            return Result<DysonGeneratedImageArtifact, string>.AsError("Generated image artifact MIME type must be image/png.");

        if (width is <= 0 or > 100_000 || height is <= 0 or > 100_000)
            return Result<DysonGeneratedImageArtifact, string>.AsError("Generated image artifact dimensions are invalid.");

        if (byteLength is <= 0 or > 100 * 1024 * 1024)
            return Result<DysonGeneratedImageArtifact, string>.AsError("Generated image artifact byte length is invalid.");

        var safeLabel = ValidateModelIdentity(modelLabel, "label");
        if (safeLabel.IsError)
            return Result<DysonGeneratedImageArtifact, string>.AsError(safeLabel.Error);

        var safeSlug = ValidateModelIdentity(modelSlug, "slug");
        if (safeSlug.IsError)
            return Result<DysonGeneratedImageArtifact, string>.AsError(safeSlug.Error);

        return Result<DysonGeneratedImageArtifact, string>.AsValue(new DysonGeneratedImageArtifact
        {
            RelativePath = safePath.Value,
            FileName = safeFileName.Value,
            MimeType = "image/png",
            Width = width,
            Height = height,
            ByteLength = byteLength,
            ModelLabel = safeLabel.Value,
            ModelSlug = safeSlug.Value,
        });
    }

    /// <summary>Revalidates a potentially deserialized artifact before it is restored into a turn.</summary>
    internal static Result<DysonGeneratedImageArtifact, string> TryRehydrate(
        DysonGeneratedImageArtifact? artifact)
    {
        if (artifact is null)
            return Result<DysonGeneratedImageArtifact, string>.AsError("Generated image artifact is missing.");

        return TryCreate(
            artifact.RelativePath,
            artifact.FileName,
            artifact.MimeType,
            artifact.Width,
            artifact.Height,
            artifact.ByteLength,
            artifact.ModelLabel,
            artifact.ModelSlug);
    }

    private static Result<string, string> ValidateRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return Result<string, string>.AsError("Generated image artifact path is required.");

        if (!string.Equals(relativePath, relativePath.Trim(), StringComparison.Ordinal)
            || relativePath.Contains('\\')
            || relativePath.IndexOf('\0') >= 0
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains(':')
            || relativePath.Contains("//", StringComparison.Ordinal)
            || !string.Equals(relativePath, relativePath.Normalize(NormalizationForm.FormC), StringComparison.Ordinal))
        {
            return Result<string, string>.AsError("Generated image artifact path must be normalized and workspace-relative.");
        }

        var prefix = RelativeDirectory + "/";
        if (!relativePath.StartsWith(prefix, StringComparison.Ordinal)
            || relativePath.Length == prefix.Length)
        {
            return Result<string, string>.AsError($"Generated image artifact path must be beneath {RelativeDirectory}/.");
        }

        var segments = relativePath.Split('/');
        if (segments.Length < 3
            || segments.Any(segment => string.IsNullOrWhiteSpace(segment)
                                       || segment is "." or ".."
                                       || segment.EndsWith('.')
                                       || segment.EndsWith(' ')
                                       || segment.Any(IsUnsafePathCharacter)))
        {
            return Result<string, string>.AsError("Generated image artifact path contains unsafe segments.");
        }

        var fileName = segments[^1];
        if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return Result<string, string>.AsError("Generated image artifact path must name a PNG file.");

        return Result<string, string>.AsValue(relativePath);
    }

    private static Result<string, string> ValidateFileName(string? fileName, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(fileName, fileName.Trim(), StringComparison.Ordinal)
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || fileName.IndexOf('\0') >= 0
            || fileName.Any(IsUnsafePathCharacter)
            || !fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(fileName, relativePath[(relativePath.LastIndexOf('/') + 1)..], StringComparison.Ordinal))
        {
            return Result<string, string>.AsError("Generated image artifact filename must match its PNG path.");
        }

        return Result<string, string>.AsValue(fileName);
    }

    private static Result<string, string> ValidateModelIdentity(string? value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > 256
            || value.Any(char.IsControl))
        {
            return Result<string, string>.AsError($"Generated image artifact model {kind} is invalid.");
        }

        return Result<string, string>.AsValue(value);
    }

    private static bool IsUnsafePathCharacter(char character) =>
        char.IsControl(character) || character is '<' or '>' or '"' or '|' or '?' or '*';
}
