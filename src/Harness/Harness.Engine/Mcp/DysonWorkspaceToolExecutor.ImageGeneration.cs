using System.Globalization;
using System.Text.Json;

namespace DysonHarness;

public sealed partial class DysonWorkspaceToolExecutor
{
    private const int GeneratedImageNameAllocationAttempts = 10_000;
    private static readonly SemaphoreSlim GeneratedImageWriteGate = new(1, 1);

    private async Task<DysonToolCallResult> GenerateImageAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var provider = _session.Config.ImageGenerationProvider;
        if (provider is null)
            return Error(call, "GenerateImage: no image-generation provider is configured for this session.");

        var request = ParseGenerateImageRequest(call);
        if (request.IsError)
            return Error(call, request.Error);

        var generated = await new OpenAiImageGenerationClient(_http)
            .GenerateAsync(provider, request.Value, cancellationToken)
            .ConfigureAwait(false);
        if (generated.IsError)
            return Error(call, $"GenerateImage: {generated.Error}");
        if (generated.Value.Images.Count == 0)
            return Error(call, "GenerateImage: the image provider returned no images.");

        var normalizedImages = new List<DysonImageGenerationNormalize.NormalizedPng>(
            generated.Value.Images.Count);
        for (var index = 0; index < generated.Value.Images.Count; index++)
        {
            var normalized = DysonImageGenerationNormalize.ToPng(generated.Value.Images[index].Bytes);
            if (normalized.IsError)
            {
                return Error(
                    call,
                    $"GenerateImage: image {index + 1} could not be normalized to PNG: {normalized.Error}");
            }

            normalizedImages.Add(normalized.Value);
        }

        var directoryCreated = await _fs
            .CreateDirectoryAsync(DysonGeneratedImageArtifact.RelativeDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (directoryCreated.IsError)
            return Error(call, $"GenerateImage: could not create artifact directory: {directoryCreated.Error}");

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var artifacts = new List<DysonGeneratedImageArtifact>(normalizedImages.Count);
        // The filesystem API does not offer create-new semantics. Serialize allocation plus write so
        // concurrent executor calls in this process cannot overwrite a same-millisecond candidate.
        await GeneratedImageWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var index = 0; index < normalizedImages.Count; index++)
            {
                var normalized = normalizedImages[index];
                var path = await AllocateGeneratedImagePathAsync(timestamp, index + 1, cancellationToken)
                    .ConfigureAwait(false);
                if (path.IsError)
                    return Error(call, $"GenerateImage: {path.Error}");

                var fileName = path.Value[(path.Value.LastIndexOf('/') + 1)..];
                var artifact = DysonGeneratedImageArtifact.TryCreate(
                    path.Value,
                    fileName,
                    DysonImageGenerationNormalize.NormalizedPng.MimeType,
                    normalized.Width,
                    normalized.Height,
                    normalized.Bytes.Length,
                    provider.DisplayAlias,
                    provider.Slug);
                if (artifact.IsError)
                    return Error(call, $"GenerateImage: {artifact.Error}");

                var written = await _fs.WriteAllBytesAsync(path.Value, normalized.Bytes, cancellationToken)
                    .ConfigureAwait(false);
                if (written.IsError)
                    return Error(call, $"GenerateImage: could not write {path.Value}: {written.Error}");

                artifacts.Add(artifact.Value);
            }
        }
        finally
        {
            GeneratedImageWriteGate.Release();
        }

        var acknowledgement = JsonSerializer.Serialize(new
        {
            artifactCount = artifacts.Count,
            outputMimeType = DysonImageGenerationNormalize.NormalizedPng.MimeType,
            modelLabel = provider.DisplayAlias,
            modelSlug = provider.Slug,
            artifacts = artifacts.Select(artifact => new
            {
                path = artifact.RelativePath,
                width = artifact.Width,
                height = artifact.Height,
                byteLength = artifact.ByteLength,
            }),
        });

        return new DysonToolCallResult
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            Content = DysonToolResultLimits.TruncateContent(acknowledgement),
            GeneratedImageArtifacts = artifacts,
        };
    }

    private Result<OpenAiImageGenerationRequest, string> ParseGenerateImageRequest(DysonToolCall call)
    {
        try
        {
            using var document = JsonDocument.Parse(ArgsOrEmpty(call));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Result<OpenAiImageGenerationRequest, string>.AsError(
                    "GenerateImage: arguments must be a JSON object.");
            }

            var prompt = RequireString(root, "prompt");
            if (prompt.IsError)
            {
                return Result<OpenAiImageGenerationRequest, string>.AsError(
                    "GenerateImage: " + prompt.Error);
            }

            var size = TryGetOptionalGenerateImageString(root, "size");
            if (size.IsError)
                return Result<OpenAiImageGenerationRequest, string>.AsError(size.Error);
            var quality = TryGetOptionalGenerateImageString(root, "quality");
            if (quality.IsError)
                return Result<OpenAiImageGenerationRequest, string>.AsError(quality.Error);
            var style = TryGetOptionalGenerateImageString(root, "style");
            if (style.IsError)
                return Result<OpenAiImageGenerationRequest, string>.AsError(style.Error);
            var background = TryGetOptionalGenerateImageString(root, "background");
            if (background.IsError)
                return Result<OpenAiImageGenerationRequest, string>.AsError(background.Error);
            var outputFormat = TryGetOptionalGenerateImageString(root, "outputFormat");
            if (outputFormat.IsError)
                return Result<OpenAiImageGenerationRequest, string>.AsError(outputFormat.Error);

            var count = 1;
            if (root.TryGetProperty("count", out var countElement))
            {
                if (countElement.ValueKind != JsonValueKind.Number || !countElement.TryGetInt32(out count))
                {
                    return Result<OpenAiImageGenerationRequest, string>.AsError(
                        "GenerateImage: count must be an integer.");
                }
            }

            var request = new OpenAiImageGenerationRequest
            {
                Prompt = prompt.Value,
                Size = size.Value,
                Quality = quality.Value,
                Style = style.Value,
                Background = background.Value,
                OutputFormat = outputFormat.Value,
                Count = count,
            }.Validate();

            return request.IsError
                ? Result<OpenAiImageGenerationRequest, string>.AsError($"GenerateImage: {request.Error}")
                : request;
        }
        catch (JsonException)
        {
            return Result<OpenAiImageGenerationRequest, string>.AsError(
                "GenerateImage: invalid JSON arguments.");
        }
    }

    private static Result<string?, string> TryGetOptionalGenerateImageString(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return Result<string?, string>.AsValue(null);

        if (value.ValueKind != JsonValueKind.String)
        {
            return Result<string?, string>.AsError(
                $"GenerateImage: {propertyName} must be a string.");
        }

        return Result<string?, string>.AsValue(value.GetString());
    }

    private async Task<Result<string, string>> AllocateGeneratedImagePathAsync(
        string timestamp,
        int startingOrdinal,
        CancellationToken cancellationToken)
    {
        for (var ordinal = startingOrdinal;
             ordinal < startingOrdinal + GeneratedImageNameAllocationAttempts;
             ordinal++)
        {
            var relativePath =
                $"{DysonGeneratedImageArtifact.RelativeDirectory}/{timestamp}-{ordinal:D2}.png";
            var exists = await _fs.FileExistsAsync(relativePath, cancellationToken).ConfigureAwait(false);
            if (exists.IsError)
                return Result<string, string>.AsError(exists.Error);
            if (!exists.Value)
                return Result<string, string>.AsValue(relativePath);
        }

        return Result<string, string>.AsError(
            "could not allocate a collision-free generated image artifact filename.");
    }
}
