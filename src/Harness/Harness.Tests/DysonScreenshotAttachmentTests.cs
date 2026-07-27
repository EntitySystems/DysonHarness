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

    private static void AssertCatalog()
    {
        var pipeline = DysonMcpPipeline.CreateDefault(
            DysonMcpAccessMode.FullAccess,
            browserControlAvailable: true);
        if (!pipeline.Tools.TryGetValue("BrowserTakeScreenshot", out var tool)
            || tool.Description.Contains("base64 in the tool result", StringComparison.Ordinal)
            || !tool.Description.Contains("multimodal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "BrowserTakeScreenshot catalog must describe multimodal ack (no base64-in-Content).");
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
            var executor = new DysonWorkspaceToolExecutor(session, root, new HttpClient());
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
            var completionsJson = completions.Messages.ToJsonString();
            if (!completionsJson.Contains("image_url", StringComparison.Ordinal)
                || !completionsJson.Contains("data:image/jpeg;base64,", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "In-flight Completions must include JPEG image_url on first emission.");
            }

            var responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds:
                [
                    new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([call], [result]),
                ]);
            var responsesJson = responses.Input.ToJsonString();
            if (!responsesJson.Contains("input_image", StringComparison.Ordinal)
                || !responsesJson.Contains("data:image/jpeg;base64,", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "In-flight Responses must include input_image on first emission.");
            }

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
            || !afterJson.Contains("data:image/jpeg;base64,", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Post-tool user message must carry JPEG image_url.");
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

    private sealed class StubTab(string id, string windowId, byte[] png) : IDysonBrowserTab
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

        public Task<Result<byte[], string>> TakeScreenshotAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<byte[], string>.AsValue(png));

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
