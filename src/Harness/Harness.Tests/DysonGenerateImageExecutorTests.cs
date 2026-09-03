using System.Net;
using System.Text;
using System.Text.Json;

using DysonHarness;
using ImageMagick;

namespace Harness.Tests;

public sealed class DysonGenerateImageExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_rejects_unavailable_tool_and_invalid_arguments_without_calling_api()
    {
        var root = CreateTempRoot();
        try
        {
            var unavailableSession = new StubSession(new DysonAgentSessionConfig());
            Assert.False(unavailableSession.McpPipeline.Tools.ContainsKey("GenerateImage"));
            var unavailable = await DysonWorkspaceTestFs.CreateExecutorAsync(
                unavailableSession,
                root,
                new HttpClient(new StubHandler(_ => throw new InvalidOperationException("No HTTP expected."))));

            var unavailableResult = await ExecuteAsync(unavailable, "{\"prompt\":\"test\"}");
            Assert.True(unavailableResult.IsError);
            Assert.Contains("not available", unavailableResult.Content, StringComparison.OrdinalIgnoreCase);

            // The executor remains defensive if a dynamic catalog source injects the tool after gating.
            unavailableSession.McpPipeline.Tools["GenerateImage"] = DysonMcpPipeline
                .CreateDefault(DysonMcpAccessMode.FullAccess)
                .Tools["GenerateImage"];
            var noConfigResult = await ExecuteAsync(unavailable, "{\"prompt\":\"test\"}");
            Assert.True(noConfigResult.IsError);
            Assert.Contains("no image-generation provider", noConfigResult.Content, StringComparison.Ordinal);

            var called = false;
            var configured = await DysonWorkspaceTestFs.CreateExecutorAsync(
                new StubSession(new DysonAgentSessionConfig { ImageGenerationProvider = DirectProvider() }),
                root,
                new HttpClient(new StubHandler(_ =>
                {
                    called = true;
                    throw new InvalidOperationException("No HTTP expected.");
                })));

            var invalidResult = await ExecuteAsync(configured, "{\"prompt\":\"test\",\"count\":\"two\"}");
            Assert.True(invalidResult.IsError);
            Assert.Contains("count must be an integer", invalidResult.Content, StringComparison.Ordinal);
            Assert.False(called);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_saves_png_artifacts_and_normalizes_jpeg_response()
    {
        var png = CreateImage(MagickFormat.Png, 19, 11);
        var jpeg = CreateImage(MagickFormat.Jpeg, 37, 23);
        var firstBase64 = Convert.ToBase64String(png);
        var secondBase64 = Convert.ToBase64String(jpeg);
        var root = CreateTempRoot();
        try
        {
            using var http = new HttpClient(new StubHandler(request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("https://api.openai.com/v1/images/generations", request.RequestUri?.ToString());
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("sk-image-test", request.Headers.Authorization?.Parameter);

                using var body = JsonDocument.Parse(request.Content!.ReadAsStream());
                Assert.Equal("gpt-image-1", body.RootElement.GetProperty("model").GetString());
                Assert.Equal("A generated test image", body.RootElement.GetProperty("prompt").GetString());
                Assert.Equal(2, body.RootElement.GetProperty("n").GetInt32());
                Assert.Equal("jpeg", body.RootElement.GetProperty("output_format").GetString());

                return Json(HttpStatusCode.OK, $$"""{"data":[{"b64_json":"{{firstBase64}}"},{"b64_json":"{{secondBase64}}"}]}""");
            }));
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(
                new StubSession(new DysonAgentSessionConfig { ImageGenerationProvider = DirectProvider() }),
                root,
                http);

            var result = await ExecuteAsync(
                executor,
                "{\"prompt\":\"A generated test image\",\"count\":2,\"outputFormat\":\"jpeg\"}");

            Assert.False(result.IsError, result.IsError ? result.Content : null);
            Assert.Null(result.BinaryAttachment);
            Assert.Equal(2, result.GeneratedImageArtifacts.Count);
            Assert.DoesNotContain(firstBase64, result.Content, StringComparison.Ordinal);
            Assert.DoesNotContain(secondBase64, result.Content, StringComparison.Ordinal);

            using var acknowledgement = JsonDocument.Parse(result.Content);
            Assert.Equal(2, acknowledgement.RootElement.GetProperty("artifactCount").GetInt32());
            Assert.Equal("image/png", acknowledgement.RootElement.GetProperty("outputMimeType").GetString());
            Assert.Equal("GPT Image 1", acknowledgement.RootElement.GetProperty("modelLabel").GetString());
            Assert.Equal("gpt-image-1", acknowledgement.RootElement.GetProperty("modelSlug").GetString());

            var artifacts = result.GeneratedImageArtifacts;
            Assert.Equal(19, artifacts[0].Width);
            Assert.Equal(11, artifacts[0].Height);
            Assert.Equal(37, artifacts[1].Width);
            Assert.Equal(23, artifacts[1].Height);
            Assert.All(artifacts, artifact =>
            {
                Assert.StartsWith(".dyson/image-gen/", artifact.RelativePath, StringComparison.Ordinal);
                Assert.Matches("^\\.dyson/image-gen/\\d{8}-\\d{9}-\\d{2}\\.png$", artifact.RelativePath);
                Assert.Equal("image/png", artifact.MimeType);
                Assert.Equal("GPT Image 1", artifact.ModelLabel);
                Assert.Equal("gpt-image-1", artifact.ModelSlug);

                var filePath = Path.Combine(root, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(filePath));
                using var saved = new MagickImage(File.ReadAllBytes(filePath));
                Assert.Equal(MagickFormat.Png, saved.Format);
                Assert.Equal(artifact.Width, (int)saved.Width);
                Assert.Equal(artifact.Height, (int)saved.Height);
                Assert.Equal(new FileInfo(filePath).Length, artifact.ByteLength);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_returns_image_client_errors_without_creating_artifacts()
    {
        var root = CreateTempRoot();
        try
        {
            using var http = new HttpClient(new StubHandler(_ =>
                Json(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"invalid request\"}}")));
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(
                new StubSession(new DysonAgentSessionConfig { ImageGenerationProvider = DirectProvider() }),
                root,
                http);

            var result = await ExecuteAsync(executor, "{\"prompt\":\"test\"}");

            Assert.True(result.IsError);
            Assert.Contains("OpenAI API 400", result.Content, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, ".dyson", "image-gen")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task<DysonToolCallResult> ExecuteAsync(
        DysonWorkspaceToolExecutor executor,
        string argumentsJson) =>
        executor.ExecuteAsync(new DysonToolCall
        {
            CallId = Guid.NewGuid().ToString("N"),
            ToolName = "GenerateImage",
            Stage = 1,
            ArgumentsJson = argumentsJson,
        });

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-generate-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static byte[] CreateImage(MagickFormat format, uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.Crimson, width, height);
        image.Format = format;
        return image.ToByteArray();
    }

    private static OpenAiCompatibleAgentProvider DirectProvider()
    {
        var provider = new DysonModelProviderEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Direct OpenAI",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-image-test",
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

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(DysonAgentSessionConfig config) : DysonAgentSession(
        DysonAgentModes.Work,
        config,
        new StubProvider())
    {
        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(string agentMode, string task, string? context = null, IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null, string? modelSlug = null, string? reasoningEffort = null, IReadOnlyList<string>? contextFiles = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override Task<VoidResult<string>> LoadFunctionalContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(VoidResult<string>.Success);
        public override Task<VoidResult<string>> PromptAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult(VoidResult<string>.Success);
        public override Task<VoidResult<string>> PromptAsync(string prompt, IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default) => Task.FromResult(VoidResult<string>.Success);
        public override Task<VoidResult<string>> PromptHarnessTurnAsync(DysonAgentTurn turn, CancellationToken cancellationToken = default) => Task.FromResult(VoidResult<string>.Success);
        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(string planRelativePath, IReadOnlyList<string>? reportBlocks = null, CancellationToken cancellationToken = default) => Task.FromResult(VoidResult<string>.Success);
        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(DysonAgentInterrupt interrupt, string? title = null, CancellationToken cancellationToken = default) => Task.FromResult(VoidResult<string>.Success);
        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(string instruction, CancellationToken cancellationToken = default) => Task.FromResult(VoidResult<string>.Success);
        public override Task<VoidResult<string>> PromptShellExitedAsync(DysonAgentInterrupt interrupt, CancellationToken cancellationToken = default) => Task.FromResult(VoidResult<string>.Success);
        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
