using DysonHarness;
using ImageMagick;

namespace Harness.Tests;

/// <summary>
/// ponytail: LoadBinary images + BrowserTakeScreenshot require FileStorage;
/// non-image LoadBinary is not gated. No live bucket.
/// </summary>
public class DysonS3ImageToolGateTests
{
    [Fact]
    public async Task BrowserTakeScreenshot_without_file_storage_is_error_without_attachment()
    {
        byte[] png;
        using (var image = new MagickImage(MagickColors.Green, 32, 24))
        {
            image.Format = MagickFormat.Png;
            png = image.ToByteArray();
        }

        var config = new DysonAgentSessionConfig
        {
            BrowserControl = new StubBrowserControl("win1", "tab1", png),
        };
        var session = new StubSession(config);
        var root = Path.Combine(Path.GetTempPath(), "dyson-s3-gate-shot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, root, new HttpClient());
            var result = await executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "shot1",
                ToolName = "BrowserTakeScreenshot",
                Stage = 0,
                ArgumentsJson = """{"windowId":"win1","tabId":"tab1"}""",
            });

            AssertGatedWithoutAttachment(result);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task LoadBinary_image_without_file_storage_is_error_without_attachment()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-s3-gate-lb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string fileName = "shot.png";
            File.WriteAllBytes(
                Path.Combine(root, fileName),
                [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01]);

            var session = new StubSession(new DysonAgentSessionConfig());
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, root, new HttpClient());
            var result = await executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "lb1",
                ToolName = "LoadBinary",
                Stage = 0,
                ArgumentsJson = $$"""{"path":"{{fileName}}"}""",
            });

            AssertGatedWithoutAttachment(result);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task LoadBinary_non_image_without_file_storage_succeeds_with_attachment()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-s3-gate-dll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string fileName = "alpha.dll";
            var bytes = new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x02 };
            File.WriteAllBytes(Path.Combine(root, fileName), bytes);

            var session = new StubSession(new DysonAgentSessionConfig());
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, root, new HttpClient());
            var result = await executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "lb-dll",
                ToolName = "LoadBinary",
                Stage = 0,
                ArgumentsJson = $$"""{"path":"{{fileName}}"}""",
            });

            if (result.IsError)
                throw new InvalidOperationException($"LoadBinary dll must succeed: {result.Content}");
            if (result.BinaryAttachment is null)
                throw new InvalidOperationException("Non-image LoadBinary must set BinaryAttachment.");
            if (result.BinaryAttachment.IsImage)
                throw new InvalidOperationException("DLL attachment must not be treated as an image.");
            if (result.BinaryAttachment.FileName != fileName
                || result.BinaryAttachment.MimeType != "application/octet-stream")
            {
                throw new InvalidOperationException("DLL attachment metadata mismatch.");
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertGatedWithoutAttachment(DysonToolCallResult result)
    {
        if (!result.IsError)
            throw new InvalidOperationException("Expected tool error when FileStorage is null.");
        if (result.BinaryAttachment is not null)
            throw new InvalidOperationException("Gated image tools must not set BinaryAttachment.");
        if (!result.Content.Contains(DysonS3FileStorage.FileStorageRequiredToken, StringComparison.Ordinal)
            || !result.Content.Contains(DysonS3FileStorage.NotConfiguredMessage, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected NotConfiguredMessage, got: {result.Content}");
        }
    }

    private static void TryDelete(string root)
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
            int? timeoutMs = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result<byte[], string>.AsValue(png));
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

    private sealed class StubSession(DysonAgentSessionConfig config) : DysonAgentSession(
        DysonAgentModes.Explore,
        config,
        new StubProvider())
    {
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
