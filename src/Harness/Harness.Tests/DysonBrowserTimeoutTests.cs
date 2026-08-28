using System.Diagnostics;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: every browser MCP tool has optional timeoutMs (default 60s);
/// engine linked CTS fails hung JS without waiting the default.
/// </summary>
public class DysonBrowserTimeoutTests
{
    [Fact]
    public void Run()
    {
        AssertCatalogHasTimeoutMs();
        AssertHungJavaScriptTimesOut();
        AssertCallerCancelIsCancelledNotTimeout();
    }

    private static void AssertCatalogHasTimeoutMs()
    {
        var pipeline = DysonMcpPipeline.CreateDefault(
            DysonMcpAccessMode.FullAccess,
            browserControlAvailable: true);
        var missing = pipeline.Tools.Values
            .Where(t => t.Name.StartsWith("Browser", StringComparison.Ordinal)
                || t.Name is "OpenBrowser" or "ListBrowserWindows" or "CloseBrowser"
                    or "ResizeBrowser" or "ListBrowserTabs" or "NewBrowserTab"
                    or "CloseBrowserTab" or "ActivateBrowserTab" or "ClearBrowserCache")
            .Where(t => !t.InputSchemaJson.Contains("timeoutMs", StringComparison.Ordinal)
                || !t.InputSchemaJson.Contains("default 60000", StringComparison.Ordinal))
            .Select(t => t.Name)
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Browser tools must expose optional timeoutMs (default 60000): "
                + string.Join(", ", missing));
        }

        if (!pipeline.Tools.TryGetValue("BrowserTakeScreenshot", out var shot)
            || shot.Description.Contains("30000", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "BrowserTakeScreenshot description must not still say default 30000.");
        }
    }

    private static void AssertHungJavaScriptTimesOut()
    {
        var config = new DysonAgentSessionConfig { BrowserControl = new HangControl() };
        var session = new StubSession(config);
        var root = Path.Combine(Path.GetTempPath(), "dyson-browser-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executor = DysonWorkspaceTestFs.CreateExecutor(session, root, new HttpClient());
            var call = new DysonToolCall
            {
                CallId = "js1",
                ToolName = "BrowserExecuteJavaScript",
                Stage = 0,
                ArgumentsJson = """{"windowId":"win1","tabId":"tab1","code":"1","timeoutMs":50}""",
            };
            var sw = Stopwatch.StartNew();
            var result = executor.ExecuteAsync(call).GetAwaiter().GetResult();
            if (!result.IsError
                || result.Content is null
                || !result.Content.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Expected timeout error, got IsError={result.IsError} Content={result.Content}");
            }

            if (sw.Elapsed >= TimeSpan.FromSeconds(2))
                throw new InvalidOperationException("Hung JS must time out quickly.");
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

    private static void AssertCallerCancelIsCancelledNotTimeout()
    {
        var config = new DysonAgentSessionConfig { BrowserControl = new HangControl() };
        var session = new StubSession(config);
        var root = Path.Combine(Path.GetTempPath(), "dyson-browser-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executor = DysonWorkspaceTestFs.CreateExecutor(session, root, new HttpClient());
            var call = new DysonToolCall
            {
                CallId = "js2",
                ToolName = "BrowserExecuteJavaScript",
                Stage = 0,
                ArgumentsJson = """{"windowId":"win1","tabId":"tab1","code":"1","timeoutMs":30000}""",
            };
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var result = executor.ExecuteAsync(call, cts.Token).GetAwaiter().GetResult();
            if (!result.IsError
                || result.Content is null
                || !result.Content.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
                || result.Content.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Expected cancelled, got IsError={result.IsError} Content={result.Content}");
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

    private sealed class HangControl : IDysonBrowserControl
    {
        private readonly HangWindow _window = new();

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
            string windowId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IDysonBrowserWindow, string>.AsValue(_window));

        public Task<Result<DysonBrowserCacheClearResult, string>> ClearBrowserCacheAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<DysonBrowserCacheClearResult, string>.AsValue(new DysonBrowserCacheClearResult()));
    }

    private sealed class HangWindow : IDysonBrowserWindow
    {
        private readonly HangTab _tab = new();

        public string Id => "win1";

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
            string tabId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<VoidResult<string>> ActivateTabAsync(
            string tabId,
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

    private sealed class HangTab : IDysonBrowserTab
    {
        public string Id => "tab1";
        public string WindowId => "win1";

        public Task<Result<string, string>> GetUrlAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string, string>.AsValue("about:blank"));

        public Task<Result<string, string>> GetTitleAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string, string>.AsValue(""));

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

        public async Task<Result<string, string>> ExecuteJavaScriptAsync(
            string code,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return Result<string, string>.AsValue("unreachable");
        }

        public Task<Result<string, string>> GetHtmlAsync(CancellationToken cancellationToken = default) =>
            ExecuteJavaScriptAsync("document.documentElement.outerHTML", cancellationToken);

        public Task<Result<byte[], string>> TakeScreenshotAsync(
            int? timeoutMs = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<byte[], string>.AsError("not used"));

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
