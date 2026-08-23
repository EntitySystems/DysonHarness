using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>Validated input for the direct OpenAI Images generation endpoint.</summary>
public sealed record OpenAiImageGenerationRequest
{
    public required string Prompt { get; init; }
    public string? Size { get; init; }
    public string? Quality { get; init; }
    public string? Style { get; init; }
    public string? Background { get; init; }
    public string? OutputFormat { get; init; }
    public int Count { get; init; } = 1;

    /// <summary>
    /// Validates and canonicalizes optional Images API settings. Optional blank strings are omitted.
    /// </summary>
    public Result<OpenAiImageGenerationRequest, string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Prompt))
            return Result<OpenAiImageGenerationRequest, string>.AsError("prompt is required.");

        var prompt = Prompt.Trim();
        if (prompt.Length > 32_000)
        {
            return Result<OpenAiImageGenerationRequest, string>.AsError(
                "prompt must be at most 32000 characters.");
        }

        if (Count is < 1 or > 10)
            return Result<OpenAiImageGenerationRequest, string>.AsError("count must be between 1 and 10.");

        var size = NormalizeChoice(Size, "size", ["auto", "1024x1024", "1024x1536", "1536x1024"]);
        if (size.IsError)
            return Result<OpenAiImageGenerationRequest, string>.AsError(size.Error);

        var quality = NormalizeChoice(Quality, "quality", ["auto", "low", "medium", "high"]);
        if (quality.IsError)
            return Result<OpenAiImageGenerationRequest, string>.AsError(quality.Error);

        var style = NormalizeChoice(Style, "style", ["vivid", "natural"]);
        if (style.IsError)
            return Result<OpenAiImageGenerationRequest, string>.AsError(style.Error);

        var background = NormalizeChoice(Background, "background", ["auto", "transparent", "opaque"]);
        if (background.IsError)
            return Result<OpenAiImageGenerationRequest, string>.AsError(background.Error);

        var outputFormat = NormalizeChoice(OutputFormat, "outputFormat", ["png", "jpeg", "webp"]);
        if (outputFormat.IsError)
            return Result<OpenAiImageGenerationRequest, string>.AsError(outputFormat.Error);

        return Result<OpenAiImageGenerationRequest, string>.AsValue(this with
        {
            Prompt = prompt,
            Size = size.Value,
            Quality = quality.Value,
            Style = style.Value,
            Background = background.Value,
            OutputFormat = outputFormat.Value,
        });
    }

    private static Result<string?, string> NormalizeChoice(
        string? value,
        string parameterName,
        IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result<string?, string>.AsValue(null);

        var normalized = value.Trim().ToLowerInvariant();
        if (allowed.Contains(normalized, StringComparer.Ordinal))
            return Result<string?, string>.AsValue(normalized);

        return Result<string?, string>.AsError(
            $"{parameterName} must be one of: {string.Join(", ", allowed)}.");
    }
}

/// <summary>One base64-decoded image returned by the OpenAI Images API.</summary>
public readonly record struct OpenAiGeneratedImage(byte[] Bytes);

/// <summary>Decoded Images API response data.</summary>
public readonly record struct OpenAiImageGenerationResult(IReadOnlyList<OpenAiGeneratedImage> Images);

/// <summary>Direct OpenAI-only client for <c>POST /images/generations</c>.</summary>
public sealed class OpenAiImageGenerationClient(HttpClient http)
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>
    /// Returns whether a provider is a credentialed, user-owned direct OpenAI endpoint.
    /// Managed proxies, OpenRouter, other compatible endpoints, and missing credentials are excluded.
    /// </summary>
    public static bool SupportsProvider(OpenAiCompatibleAgentProvider? provider) =>
        OpenAiImageGenerationEligibility.IsEligible(provider);

    /// <summary>
    /// Sends a validated image generation request using <paramref name="provider"/>'s configured model.
    /// The model is never caller-controlled.
    /// </summary>
    public async Task<Result<OpenAiImageGenerationResult, string>> GenerateAsync(
        OpenAiCompatibleAgentProvider provider,
        OpenAiImageGenerationRequest imageRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(imageRequest);

        if (!SupportsProvider(provider))
        {
            return Result<OpenAiImageGenerationResult, string>.AsError(
                "Image generation requires a credentialed direct OpenAI provider at https://api.openai.com/v1.");
        }

        if (string.IsNullOrWhiteSpace(provider.Slug))
            return Result<OpenAiImageGenerationResult, string>.AsError("Image generation provider model is required.");

        var validatedRequest = imageRequest.Validate();
        if (validatedRequest.IsError)
            return Result<OpenAiImageGenerationResult, string>.AsError(validatedRequest.Error);

        var body = BuildRequestBody(provider.Slug, validatedRequest.Value);
        var baseUrl = OpenAiCompatibleHttp.NormalizeBaseUrl(provider.BaseUrl);
        var response = await OpenAiCompatibleHttp
            .SendJsonAsync(
                _http,
                HttpMethod.Post,
                $"{baseUrl}/images/generations",
                provider.ApiKey,
                body,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.IsError)
            return Result<OpenAiImageGenerationResult, string>.AsError(response.Error);

        return Parse(response.Value);
    }

    /// <summary>Builds the Images API JSON body after request validation.</summary>
    public static JsonObject BuildRequestBody(string model, OpenAiImageGenerationRequest imageRequest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(imageRequest);

        var body = new JsonObject
        {
            ["model"] = model.Trim(),
            ["prompt"] = imageRequest.Prompt,
            ["n"] = imageRequest.Count,
        };

        AddOptional(body, "size", imageRequest.Size);
        AddOptional(body, "quality", imageRequest.Quality);
        AddOptional(body, "style", imageRequest.Style);
        AddOptional(body, "background", imageRequest.Background);
        AddOptional(body, "output_format", imageRequest.OutputFormat);
        return body;
    }

    /// <summary>Parses the <c>data[].b64_json</c> response shape and decodes each image.</summary>
    public static Result<OpenAiImageGenerationResult, string> Parse(JsonObject response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response["data"] is not JsonArray data || data.Count == 0)
        {
            return Result<OpenAiImageGenerationResult, string>.AsError(
                "OpenAI Images response must contain a non-empty data array.");
        }

        var images = new List<OpenAiGeneratedImage>(data.Count);
        for (var index = 0; index < data.Count; index++)
        {
            if (data[index] is not JsonObject item
                || item["b64_json"] is not JsonValue base64Node
                || !base64Node.TryGetValue<string>(out var base64)
                || string.IsNullOrWhiteSpace(base64))
            {
                return Result<OpenAiImageGenerationResult, string>.AsError(
                    $"OpenAI Images response data[{index}] is missing b64_json.");
            }

            try
            {
                images.Add(new OpenAiGeneratedImage(Convert.FromBase64String(base64)));
            }
            catch (FormatException ex)
            {
                return Result<OpenAiImageGenerationResult, string>.AsError(
                    $"OpenAI Images response data[{index}] has invalid b64_json: {ex.Message}");
            }
        }

        return Result<OpenAiImageGenerationResult, string>.AsValue(new OpenAiImageGenerationResult(images));
    }

    private static void AddOptional(JsonObject body, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            body[name] = value;
    }
}
