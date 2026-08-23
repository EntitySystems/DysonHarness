using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using DysonHarness;
using ImageMagick;

namespace Harness.Tests;

public sealed class OpenAiImageGenerationClientTests
{
    [Fact]
    public async Task GenerateAsync_serializes_direct_OpenAi_request_and_decodes_images()
    {
        var sourceBytes = Encoding.UTF8.GetBytes("image bytes");
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.openai.com/v1/images/generations", request.RequestUri?.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("sk-test", request.Headers.Authorization?.Parameter);

            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()) as JsonObject;
            Assert.NotNull(body);
            Assert.Equal("gpt-image-1", body!["model"]?.GetValue<string>());
            Assert.Equal("A violet test image", body["prompt"]?.GetValue<string>());
            Assert.Equal(2, body["n"]?.GetValue<int>());
            Assert.Equal("1024x1024", body["size"]?.GetValue<string>());
            Assert.Equal("high", body["quality"]?.GetValue<string>());
            Assert.Equal("vivid", body["style"]?.GetValue<string>());
            Assert.Equal("transparent", body["background"]?.GetValue<string>());
            Assert.Equal("jpeg", body["output_format"]?.GetValue<string>());

            var base64 = Convert.ToBase64String(sourceBytes);
            return Json(HttpStatusCode.OK, $$"""{"data":[{"b64_json":"{{base64}}"},{"b64_json":"{{base64}}"}]}""");
        });
        using var http = new HttpClient(handler);
        var client = new OpenAiImageGenerationClient(http);

        var result = await client.GenerateAsync(
            DirectProvider(),
            new OpenAiImageGenerationRequest
            {
                Prompt = " A violet test image ",
                Size = "1024x1024",
                Quality = "HIGH",
                Style = "Vivid",
                Background = "transparent",
                OutputFormat = "JPEG",
                Count = 2,
            });

        Assert.False(result.IsError, result.IsError ? result.Error : null);
        Assert.Equal(2, result.Value.Images.Count);
        Assert.All(result.Value.Images, image => Assert.Equal(sourceBytes, image.Bytes));
    }

    [Theory]
    [InlineData("", null, null, null, null, null, 1, "prompt is required")]
    [InlineData("test", "512x512", null, null, null, null, 1, "size must be one of")]
    [InlineData("test", null, "ultra", null, null, null, 1, "quality must be one of")]
    [InlineData("test", null, null, "cinematic", null, null, 1, "style must be one of")]
    [InlineData("test", null, null, null, "blue", null, 1, "background must be one of")]
    [InlineData("test", null, null, null, null, "gif", 1, "outputFormat must be one of")]
    [InlineData("test", null, null, null, null, null, 0, "count must be between 1 and 10")]
    public void Request_validation_rejects_unsupported_values(
        string prompt,
        string? size,
        string? quality,
        string? style,
        string? background,
        string? outputFormat,
        int count,
        string error)
    {
        var result = new OpenAiImageGenerationRequest
        {
            Prompt = prompt,
            Size = size,
            Quality = quality,
            Style = style,
            Background = background,
            OutputFormat = outputFormat,
            Count = count,
        }.Validate();

        Assert.True(result.IsError);
        Assert.Contains(error, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_returns_expected_failures_without_dispatching_unsupported_providers()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("No HTTP expected.")));
        var client = new OpenAiImageGenerationClient(http);
        var provider = DirectProvider(baseUrl: "https://example.test/v1");

        Assert.False(OpenAiImageGenerationClient.SupportsProvider(provider));

        var result = await client.GenerateAsync(provider, new OpenAiImageGenerationRequest { Prompt = "test" });

        Assert.True(result.IsError);
        Assert.Contains("direct OpenAI", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"invalid request\"}}", "OpenAI API 400")]
    [InlineData(HttpStatusCode.OK, "not json", "Invalid JSON from OpenAI API")]
    public async Task GenerateAsync_returns_api_and_json_failures(
        HttpStatusCode status,
        string body,
        string expectedError)
    {
        using var http = new HttpClient(new StubHandler(_ => Json(status, body)));
        var client = new OpenAiImageGenerationClient(http);

        var result = await client.GenerateAsync(
            DirectProvider(),
            new OpenAiImageGenerationRequest { Prompt = "test" });

        Assert.True(result.IsError);
        Assert.Contains(expectedError, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"data\":[]}", "non-empty data array")]
    [InlineData("{\"data\":[{}]}", "missing b64_json")]
    [InlineData("{\"data\":[{\"b64_json\":42}]}", "missing b64_json")]
    [InlineData("{\"data\":[{\"b64_json\":\"not-base64\"}]}", "invalid b64_json")]
    public void Parse_returns_expected_response_shape_and_base64_errors(string json, string expectedError)
    {
        var response = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("Fixture must be object.");
        var result = OpenAiImageGenerationClient.Parse(response);

        Assert.True(result.IsError);
        Assert.Contains(expectedError, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_to_png_decodes_Jpeg_and_reports_dimensions()
    {
        byte[] jpeg;
        using (var source = new MagickImage(MagickColors.Crimson, 37, 23))
        {
            source.Format = MagickFormat.Jpeg;
            jpeg = source.ToByteArray();
        }

        var normalized = DysonImageGenerationNormalize.ToPng(jpeg);

        Assert.False(normalized.IsError, normalized.IsError ? normalized.Error : null);
        Assert.Equal(37, normalized.Value.Width);
        Assert.Equal(23, normalized.Value.Height);
        Assert.True(normalized.Value.Bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));

        using var check = new MagickImage(normalized.Value.Bytes);
        Assert.Equal(MagickFormat.Png, check.Format);
    }

    [Fact]
    public void Normalize_to_png_returns_expected_invalid_image_error()
    {
        var result = DysonImageGenerationNormalize.ToPng(Encoding.UTF8.GetBytes("not an image"));

        Assert.True(result.IsError);
        Assert.Contains("normalization failed", result.Error, StringComparison.Ordinal);
    }

    private static OpenAiCompatibleAgentProvider DirectProvider(string? baseUrl = null)
    {
        var provider = new DysonModelProviderEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Direct OpenAI",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = baseUrl ?? "https://api.openai.com/v1",
            ApiKey = "sk-test",
        };
        var slug = new DysonModelSlugEntity
        {
            Id = Guid.NewGuid(),
            ProviderId = provider.Id,
            Slug = "gpt-image-1",
            DisplayAlias = "GPT Image 1",
            Provider = provider,
        };
        return new OpenAiCompatibleAgentProvider(provider, slug);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
