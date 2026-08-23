using System.Text.Json;
using System.Text.Json.Nodes;

using DysonHarness;
using Harness.UI.Demo;
using ImageMagick;

namespace Harness.Tests;

/// <summary>
/// ponytail: BrowserTakeScreenshot BinaryAttachment + Magick compress + one-shot multimodal.
/// </summary>
public class DysonScreenshotAttachmentTests
{
    [Fact]
    public void Run()
    {
        AssertCatalog();
        AssertImageCompress();
        AssertScreenshotExecutorAndOneShotTranscript();
        AssertParallelToolRoundKeepsToolMessagesConsecutive();
        AssertScreenshotUiSummary();
    }

    /// <summary>
    /// Cancelled token or expired timeoutMs must fail quickly (same race as Cef TakeScreenshot).
    /// </summary>
    [Fact]
    public async Task TakeScreenshot_CancelOrTimeout_FailsWithoutHanging()
    {
        var tab = new StubTab("tab1", "win1", [], hangUntilCancel: true);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelSw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tab.TakeScreenshotAsync(timeoutMs: 30_000, cancelled.Token));
        if (cancelSw.Elapsed >= TimeSpan.FromSeconds(2))
            throw new InvalidOperationException("Cancelled screenshot must not hang.");

        var timeoutSw = System.Diagnostics.Stopwatch.StartNew();
        var result = await tab.TakeScreenshotAsync(timeoutMs: 50, CancellationToken.None);
        if (!result.IsError
            || !result.Error.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Expected timeout error, got IsError={result.IsError} Error={result.Error}");
        }

        if (timeoutSw.Elapsed >= TimeSpan.FromSeconds(2))
            throw new InvalidOperationException("Timed-out screenshot must not hang.");
    }

    private static void AssertCatalog()
    {
        var pipeline = DysonMcpPipeline.CreateDefault(
            DysonMcpAccessMode.FullAccess,
            browserControlAvailable: true);
        if (!pipeline.Tools.TryGetValue("BrowserTakeScreenshot", out var tool)
            || tool.Description.Contains("base64 in the tool result", StringComparison.Ordinal)
            || !tool.Description.Contains("multimodal", StringComparison.OrdinalIgnoreCase)
            || !tool.InputSchemaJson.Contains("timeoutMs", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "BrowserTakeScreenshot catalog must describe multimodal ack (no base64-in-Content) and timeoutMs.");
        }
    }

    private static void AssertImageCompress()
    {
        byte[] png;
        using (var image = new MagickImage(MagickColors.Red, 2000, 1000))
        {
            image.Format = MagickFormat.Png;
            png = image.ToByteArray();
        }

        var compressed = DysonImageCompress.ToJpegMaxEdge(png);
        if (compressed.MimeType != "image/jpeg")
            throw new InvalidOperationException("Compress must emit image/jpeg.");
        if (compressed.Width > DysonImageCompress.DefaultMaxEdge
            || compressed.Height > DysonImageCompress.DefaultMaxEdge)
        {
            throw new InvalidOperationException(
                $"Longest edge must be ≤ {DysonImageCompress.DefaultMaxEdge}.");
        }

        if (compressed.Width != 1280 || compressed.Height != 640)
        {
            throw new InvalidOperationException(
                $"Expected 1280x640 after shrink, got {compressed.Width}x{compressed.Height}.");
        }

        if (compressed.Bytes.Length == 0)
            throw new InvalidOperationException("JPEG bytes must be non-empty.");

        // No upscale for small images.
        byte[] smallPng;
        using (var image = new MagickImage(MagickColors.Blue, 100, 80))
        {
            image.Format = MagickFormat.Png;
            smallPng = image.ToByteArray();
        }

        var small = DysonImageCompress.ToJpegMaxEdge(smallPng);
        if (small.Width != 100 || small.Height != 80)
            throw new InvalidOperationException("Small images must not be upscaled.");
    }

    private static void AssertScreenshotExecutorAndOneShotTranscript()
    {
        byte[] png;
        using (var image = new MagickImage(MagickColors.Green, 800, 600))
        {
            image.Format = MagickFormat.Png;
            png = image.ToByteArray();
        }

        var config = new DysonAgentSessionConfig
        {
            BrowserControl = new StubBrowserControl("win1", "tab1", png),
        };
        var session = new StubSession(config);

        var root = Path.Combine(Path.GetTempPath(), "dyson-shot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executor = DysonWorkspaceTestFs.CreateExecutor(session, root, new HttpClient());
            var call = new DysonToolCall
            {
                CallId = "shot1",
                ToolName = "BrowserTakeScreenshot",
                Stage = 0,
                ArgumentsJson = """{"windowId":"win1","tabId":"tab1"}""",
            };

            var result = executor.ExecuteAsync(call).GetAwaiter().GetResult();
            if (result.IsError)
                throw new InvalidOperationException($"BrowserTakeScreenshot failed: {result.Content}");

            if (result.BinaryAttachment is null)
                throw new InvalidOperationException("Screenshot must set BinaryAttachment.");

            var att = result.BinaryAttachment;
            if (att.MimeType != "image/jpeg"
                || att.Extension != ".jpg"
                || string.IsNullOrEmpty(att.Base64Data)
                || result.Content.Contains(att.Base64Data, StringComparison.Ordinal)
                || result.Content.Contains("\"base64\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Screenshot Content must be short ack without base64; attachment is JPEG.");
            }

            using var ack = JsonDocument.Parse(result.Content);
            if (ack.RootElement.GetProperty("mimeType").GetString() != "image/jpeg"
                || ack.RootElement.GetProperty("windowId").GetString() != "win1"
                || ack.RootElement.GetProperty("tabId").GetString() != "tab1"
                || !ack.RootElement.TryGetProperty("width", out _)
                || !ack.RootElement.TryGetProperty("height", out _)
                || !ack.RootElement.TryGetProperty("byteLength", out _))
            {
                throw new InvalidOperationException("Screenshot ack JSON metadata mismatch.");
            }

            // First look (in-flight): multimodal parts present.
            var completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds:
                [
                    new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([call], [result]),
                ]);
            AssertCompletionsImageShape(completions.Messages, expectDataUrlPrefix: "data:image/jpeg;base64,");

            var responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds:
                [
                    new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([call], [result]),
                ]);
            AssertResponsesImageDataUrlFallback(responses.Input, expectDataUrlPrefix: "data:image/jpeg;base64,");

            // Mocked Files upload → Responses prefer file_id (no data URL).
            AssertResponsesImageFileIdViaUpload(session, call, result);

            // Later in-flight round: prior screenshot round must not re-emit the image.
            var laterCall = new DysonToolCall
            {
                CallId = "other1",
                ToolName = "GetDateTime",
                Stage = 1,
                ArgumentsJson = "{}",
            };
            var laterResult = new DysonToolCallResult
            {
                CallId = "other1",
                ToolName = "GetDateTime",
                Stage = 1,
                Content = """{"utc":"2026-01-01T00:00:00Z"}""",
            };
            var multiRound = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds:
                [
                    new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([call], [result]),
                    new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([laterCall], [laterResult]),
                ]);
            if (multiRound.Messages.ToJsonString().Contains("image_url", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Prior screenshot round must not re-emit image_url after a later in-flight round.");
            }

            // History after assistant reply: ack only.
            var turn = new DysonAgentTurn
            {
                Instruction = "look",
                Kind = DysonAgentTurnKind.Normal,
            };
            turn.ToolCalls.Add(call);
            turn.ResponseLog.Enqueue(result);
            turn.AssistantText = "Saw the page.";
            session.AddTurnForTest(turn);

            var historyCompletions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session,
                currentUserPrompt: null,
                currentFilePaths: null);
            var historyJson = historyCompletions.Messages.ToJsonString();
            if (historyJson.Contains("image_url", StringComparison.Ordinal)
                || historyJson.Contains("data:image/jpeg;base64,", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Completed history must not re-include multimodal image parts.");
            }

            if (!historyJson.Contains("image/jpeg", StringComparison.Ordinal)
                || !historyJson.Contains("byteLength", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Completed history must still include the short screenshot ack JSON.");
            }

            turn.FinishStreaming();
            if (turn.ResponseLog.Any(r => r.BinaryAttachment is not null))
            {
                throw new InvalidOperationException(
                    "FinishStreaming must clear BinaryAttachment after assistant text.");
            }

            var persisted = DysonTurnToolStateSerializer.CaptureFromTurn(turn);
            if (persisted.Contains("base64Data", StringComparison.OrdinalIgnoreCase)
                || persisted.Contains("BinaryAttachment", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Persisted tool state must omit BinaryAttachment after turn complete.");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }

    /// <summary>
    /// Completions must keep role:tool consecutive after parallel tool_calls even when
    /// BrowserTakeScreenshot (BinaryAttachment) is not last in the round.
    /// </summary>
    private static void AssertParallelToolRoundKeepsToolMessagesConsecutive()
    {
        var session = new StubSession();
        var shellCall = new DysonToolCall
        {
            CallId = "shell1",
            ToolName = "ShellExecute",
            Stage = 0,
            ArgumentsJson = """{"command":"echo hi"}""",
        };
        var shotCall = new DysonToolCall
        {
            CallId = "shot1",
            ToolName = "BrowserTakeScreenshot",
            Stage = 0,
            ArgumentsJson = """{"windowId":"win1","tabId":"tab1"}""",
        };
        var reloadCall = new DysonToolCall
        {
            CallId = "reload1",
            ToolName = "BrowserReload",
            Stage = 0,
            ArgumentsJson = """{"windowId":"win1","tabId":"tab1"}""",
        };

        var shellResult = new DysonToolCallResult
        {
            CallId = "shell1",
            ToolName = "ShellExecute",
            Stage = 0,
            Content = """{"exitCode":0,"stdout":"hi"}""",
        };
        var shotResult = new DysonToolCallResult
        {
            CallId = "shot1",
            ToolName = "BrowserTakeScreenshot",
            Stage = 0,
            Content = """{"mimeType":"image/jpeg","byteLength":12,"width":8,"height":6,"windowId":"win1","tabId":"tab1"}""",
            BinaryAttachment = new DysonBinaryAttachment
            {
                FileName = "screenshot.jpg",
                Extension = ".jpg",
                MimeType = "image/jpeg",
                Base64Data = Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0xD9]),
            },
        };
        var reloadResult = new DysonToolCallResult
        {
            CallId = "reload1",
            ToolName = "BrowserReload",
            Stage = 0,
            Content = """{"ok":true}""",
        };

        var built = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds:
            [
                new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound(
                    [shellCall, shotCall, reloadCall],
                    [shellResult, shotResult, reloadResult]),
            ]);

        var expectedIds = new[] { "shell1", "shot1", "reload1" };
        var toolBlockStart = -1;
        for (var i = 0; i < built.Messages.Count; i++)
        {
            var msg = built.Messages[i] as JsonObject
                ?? throw new InvalidOperationException("Expected message object.");
            if (msg["role"]?.GetValue<string>() != "assistant"
                || msg["tool_calls"] is not JsonArray toolCalls
                || toolCalls.Count != expectedIds.Length)
            {
                continue;
            }

            toolBlockStart = i + 1;
            break;
        }

        if (toolBlockStart < 0)
            throw new InvalidOperationException("Expected assistant tool_calls for the parallel round.");

        for (var i = 0; i < expectedIds.Length; i++)
        {
            var msg = built.Messages[toolBlockStart + i] as JsonObject
                ?? throw new InvalidOperationException("Expected tool message object.");
            if (msg["role"]?.GetValue<string>() != "tool")
            {
                throw new InvalidOperationException(
                    $"Expected consecutive role:tool at index {toolBlockStart + i}; got {msg["role"]}.");
            }

            if (msg["tool_call_id"]?.GetValue<string>() != expectedIds[i])
            {
                throw new InvalidOperationException(
                    $"tool_call_id mismatch at {i}: expected {expectedIds[i]}, got {msg["tool_call_id"]}.");
            }
        }

        var afterTools = built.Messages[toolBlockStart + expectedIds.Length] as JsonObject
            ?? throw new InvalidOperationException("Expected multimodal user message after tool block.");
        if (afterTools["role"]?.GetValue<string>() != "user")
        {
            throw new InvalidOperationException(
                "JPEG image_url user message must follow the full consecutive tool block.");
        }

        var afterJson = afterTools.ToJsonString();
        if (!afterJson.Contains("image_url", StringComparison.Ordinal)
            || !afterJson.Contains("data:image/jpeg;base64,", StringComparison.Ordinal)
            || afterJson.Contains("\"filename\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Post-tool user message must carry nested JPEG image_url without filename.");
        }

        AssertCompletionsImageShape(
            built.Messages,
            expectDataUrlPrefix: "data:image/jpeg;base64,");
    }

    private static void AssertCompletionsImageShape(JsonArray messages, string expectDataUrlPrefix)
    {
        JsonObject? imagePart = null;
        foreach (var node in messages)
        {
            if (node is not JsonObject msg
                || msg["role"]?.GetValue<string>() != "user"
                || msg["content"] is not JsonArray parts)
            {
                continue;
            }

            foreach (var part in parts)
            {
                if (part is JsonObject p
                    && string.Equals(p["type"]?.GetValue<string>(), "image_url", StringComparison.Ordinal))
                {
                    imagePart = p;
                    break;
                }
            }

            if (imagePart is not null)
                break;
        }

        if (imagePart is null)
            throw new InvalidOperationException("Completions must include an image_url part.");

        if (imagePart["filename"] is not null)
            throw new InvalidOperationException("Completions image_url must not carry top-level filename.");

        if (imagePart["image_url"] is not JsonObject nested
            || nested["url"]?.GetValue<string>() is not { } url
            || !url.StartsWith(expectDataUrlPrefix, StringComparison.Ordinal)
            || nested["detail"]?.GetValue<string>() != "auto")
        {
            throw new InvalidOperationException(
                "Completions image_url must be nested { url: data URL, detail: auto }.");
        }
    }

    private static void AssertResponsesImageDataUrlFallback(JsonArray input, string expectDataUrlPrefix)
    {
        var imagePart = FindResponsesInputImage(input)
            ?? throw new InvalidOperationException("Responses must include an input_image part.");

        if (imagePart["filename"] is not null)
            throw new InvalidOperationException("Responses input_image must not carry filename.");
        if (imagePart["file_id"] is not null)
            throw new InvalidOperationException("Fallback Responses input_image must not set file_id.");

        if (imagePart["image_url"]?.GetValue<string>() is not { } url
            || !url.StartsWith(expectDataUrlPrefix, StringComparison.Ordinal)
            || imagePart["detail"]?.GetValue<string>() != "auto")
        {
            throw new InvalidOperationException(
                "Responses fallback input_image must use top-level image_url string + detail auto.");
        }
    }

    private static void AssertResponsesImageFileIdViaUpload(
        StubSession session,
        DysonToolCall call,
        DysonToolCallResult result)
    {
        var attachment = result.BinaryAttachment
            ?? throw new InvalidOperationException("Expected BinaryAttachment for upload test.");
        attachment.FileId = null;

        var handler = new StubFilesUploadHandler("file-vision-shot-1");
        using var http = new HttpClient(handler);
        var entity = new DysonModelProviderEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "test",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-test",
            OpenAiApiMode = DysonOpenAiApiModes.Responses,
        };
        var provider = new OpenAiCompatibleAgentProvider(
            entity,
            new DysonModelSlugEntity
            {
                Id = Guid.NewGuid(),
                ProviderId = entity.Id,
                Slug = "gpt-4o",
                DisplayAlias = "gpt-4o",
                Provider = entity,
            });

        OpenAiFilesClient.EnsureBinaryFileIdsAsync(
            http,
            provider,
            [result],
            onNote: null).GetAwaiter().GetResult();

        if (attachment.FileId != "file-vision-shot-1")
            throw new InvalidOperationException($"Expected FileId after upload; got {attachment.FileId}.");
        if (handler.LastPurpose != "vision")
            throw new InvalidOperationException($"Expected purpose=vision; got {handler.LastPurpose}.");

        var responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds:
            [
                new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([call], [result]),
            ]);

        var imagePart = FindResponsesInputImage(responses.Input)
            ?? throw new InvalidOperationException("Responses must include input_image after upload.");
        if (imagePart["file_id"]?.GetValue<string>() != "file-vision-shot-1"
            || imagePart["detail"]?.GetValue<string>() != "auto"
            || imagePart["image_url"] is not null
            || imagePart["filename"] is not null
            || responses.Input.ToJsonString().Contains("data:image/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Responses with FileId must emit input_image.file_id + detail and no data URL/filename.");
        }

        // Leave attachment without FileId so later history assertions stay on the data-URL path.
        attachment.FileId = null;
    }

    private static JsonObject? FindResponsesInputImage(JsonArray input)
    {
        foreach (var node in input)
        {
            if (node is not JsonObject msg || msg["content"] is not JsonArray parts)
                continue;

            foreach (var part in parts)
            {
                if (part is JsonObject p
                    && string.Equals(p["type"]?.GetValue<string>(), "input_image", StringComparison.Ordinal))
                {
                    return p;
                }
            }
        }

        return null;
    }

    private sealed class StubFilesUploadHandler(string fileId) : HttpMessageHandler
    {
        public string? LastPurpose { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post
                || request.RequestUri is null
                || !request.RequestUri.AbsolutePath.EndsWith("/files", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                {
                    Content = new StringContent("unexpected request"),
                };
            }

            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    if (part.Headers.ContentDisposition?.Name?.Trim('"') == "purpose")
                        LastPurpose = await part.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"id":"{{fileId}}","object":"file","purpose":"vision"}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private static void AssertScreenshotUiSummary()
    {

        var ack = """{"mimeType":"image/jpeg","byteLength":20480,"width":1280,"height":720,"windowId":"w","tabId":"t"}""";
        var summary = DysonToolCallUi.GetCollapsedSummary(
            "BrowserTakeScreenshot",
            """{"windowId":"w","tabId":"t"}""",
            ack,
            hasResult: true);
        if (summary.Text is null
            || !summary.Text.Contains("screenshot", StringComparison.Ordinal)
            || !summary.Text.Contains("1280x720", StringComparison.Ordinal)
            || !summary.Text.Contains("KB", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Screenshot summary mismatch: {summary.Text}");
        }

        var parsed = DysonToolCallUi.TryParseScreenshotAck(ack)
            ?? throw new InvalidOperationException("TryParseScreenshotAck failed.");
        if (parsed.Width != 1280 || parsed.Height != 720 || parsed.ByteLength != 20480)
            throw new InvalidOperationException("Screenshot ack parse mismatch.");
    }

    private sealed class StubBrowserControl(string windowId, string tabId, byte[] png) : IDysonBrowserControl
    {
        private readonly StubWindow _window = new(windowId, tabId, png);

#pragma warning disable CS0067
        public event Action<DysonBrowserSnipPayload>? SnipCaptured;
#pragma warning restore CS0067

        public Task<Result<IDysonBrowserWindow, string>> OpenBrowserAsync(
            string? url = null,
            int? width = null,
            int? height = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IDysonBrowserWindow, string>.AsValue(_window));

        public Task<Result<IReadOnlyList<IDysonBrowserWindow>, string>> ListWindowsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Result<IReadOnlyList<IDysonBrowserWindow>, string>.AsValue(
                    new IDysonBrowserWindow[] { _window }));

        public Task<Result<IDysonBrowserWindow, string>> GetWindowAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            string.Equals(id, _window.Id, StringComparison.Ordinal)
                ? Task.FromResult(Result<IDysonBrowserWindow, string>.AsValue(_window))
                : Task.FromResult(Result<IDysonBrowserWindow, string>.AsError($"Window not found: {id}"));

        public Task<Result<DysonBrowserCacheClearResult, string>> ClearBrowserCacheAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<DysonBrowserCacheClearResult, string>.AsValue(new DysonBrowserCacheClearResult
            {
                Windows = 1,
                TabsReloaded = 1,
            }));
    }

    private sealed class StubWindow(string windowId, string tabId, byte[] png) : IDysonBrowserWindow
    {
        private readonly StubTab _tab = new(tabId, windowId, png);

        public string Id { get; } = windowId;

        public Task<Result<IReadOnlyList<IDysonBrowserTab>, string>> ListTabsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Result<IReadOnlyList<IDysonBrowserTab>, string>.AsValue(
                    new IDysonBrowserTab[] { _tab }));

        public Task<Result<IDysonBrowserTab, string>> NewTabAsync(
            string? url = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IDysonBrowserTab, string>.AsValue(_tab));

        public Task<VoidResult<string>> CloseTabAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<VoidResult<string>> ActivateTabAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<VoidResult<string>> CloseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<VoidResult<string>> ResizeAsync(
            int width,
            int height,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<VoidResult<string>> BringToFrontAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);
    }

    private sealed class StubTab(string id, string windowId, byte[] png, bool hangUntilCancel = false) : IDysonBrowserTab
    {
        public string Id { get; } = id;
        public string WindowId { get; } = windowId;

        public Task<Result<string, string>> GetUrlAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string, string>.AsValue("about:blank"));

        public Task<Result<string, string>> GetTitleAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string, string>.AsValue("stub"));

        public Task<VoidResult<string>> NavigateAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<VoidResult<string>> ReloadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<VoidResult<string>> GoBackAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<VoidResult<string>> GoForwardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<VoidResult<string>> ClickAsync(
            DysonBrowserClickRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<VoidResult<string>> TypeAsync(
            DysonBrowserTypeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<VoidResult<string>> FillAsync(
            string selector,
            string value,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<VoidResult<string>> HoverAsync(
            string selector,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<VoidResult<string>> PressKeyAsync(
            DysonBrowserKeyRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<VoidResult<string>> WaitForSelectorAsync(
            string selector,
            int? timeoutMs = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<VoidResult<string>> WaitForNavigationAsync(
            int? timeoutMs = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<Result<string, string>> ExecuteJavaScriptAsync(
            string code,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string, string>.AsValue("null"));

        public Task<Result<string, string>> GetHtmlAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string, string>.AsValue("<html></html>"));

        public async Task<Result<byte[], string>> TakeScreenshotAsync(
            int? timeoutMs = null,
            CancellationToken cancellationToken = default)
        {
            if (!hangUntilCancel)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Result<byte[], string>.AsValue(png);
            }

            // Mirror Cef race: linked CT + CancelAfter vs hanging work (no STA host).
            var timeout = timeoutMs is > 0 ? timeoutMs.Value : 30_000;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            linked.Token.ThrowIfCancellationRequested();

            try
            {
                var hangTask = Task.Delay(Timeout.Infinite, CancellationToken.None);
                var delayTask = Task.Delay(Timeout.Infinite, linked.Token);
                var winner = await Task.WhenAny(hangTask, delayTask).ConfigureAwait(false);
                if (winner != hangTask)
                {
                    if (cancellationToken.IsCancellationRequested)
                        cancellationToken.ThrowIfCancellationRequested();
                    return Result<byte[], string>.AsError($"Screenshot timed out after {timeout}ms.");
                }

                return Result<byte[], string>.AsError("unreachable");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Result<byte[], string>.AsError($"Screenshot timed out after {timeout}ms.");
            }
        }

        public Task<Result<IReadOnlyList<DysonBrowserConsoleEntry>, string>> ReadConsoleLogAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Result<IReadOnlyList<DysonBrowserConsoleEntry>, string>.AsValue(
                    Array.Empty<DysonBrowserConsoleEntry>()));

        public Task<Result<IReadOnlyList<DysonBrowserNetworkEntry>, string>> ReadNetworkLogAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Result<IReadOnlyList<DysonBrowserNetworkEntry>, string>.AsValue(
                    Array.Empty<DysonBrowserNetworkEntry>()));

        public Task<VoidResult<string>> ClearConsoleLogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<VoidResult<string>> ClearNetworkLogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession : DysonAgentSession
    {
        public StubSession()
            : this(new DysonAgentSessionConfig())
        {
        }

        public StubSession(DysonAgentSessionConfig config)
            : base(DysonAgentModes.Explore, config, new StubProvider())
        {
        }

        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
            IReadOnlyList<string>? contextFiles = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> LoadFunctionalContextAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
