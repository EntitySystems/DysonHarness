using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: ClearBrowserCache MCP catalog + executor path over a recording fake control.
/// </summary>
public class DysonClearBrowserCacheTests
{
    [Fact]
    public async Task Run()
    {
        AssertCatalog();
        await AssertExecutorInvokesControl();
        AssertNullControlUnavailable();
    }

    private static void AssertCatalog()
    {
        var pipeline = DysonMcpPipeline.CreateDefault(
            DysonMcpAccessMode.FullAccess,
            browserControlAvailable: true);
        if (!pipeline.Tools.TryGetValue("ClearBrowserCache", out var tool)
            || !tool.Description.Contains("HTTP cache", StringComparison.OrdinalIgnoreCase)
            || !tool.Description.Contains("cef-cache", StringComparison.OrdinalIgnoreCase)
            || !tool.InputSchemaJson.Contains("timeoutMs", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "ClearBrowserCache must be cataloged with timeoutMs and shared cef-cache note.");
        }

        var withoutBrowser = DysonMcpPipeline.CreateDefault(
            DysonMcpAccessMode.FullAccess,
            browserControlAvailable: false);
        if (withoutBrowser.Tools.ContainsKey("ClearBrowserCache"))
            throw new InvalidOperationException("ClearBrowserCache must be omitted when BrowserControl is unavailable.");
    }

    private static async Task AssertExecutorInvokesControl()
    {
        var control = new RecordingBrowserControl(windows: 2, tabsReloaded: 3);
        var config = new DysonAgentSessionConfig { BrowserControl = control };
        var session = new StubSession(config);

        var root = Path.Combine(Path.GetTempPath(), "dyson-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, root, new HttpClient());
            var call = new DysonToolCall
            {
                CallId = "cache1",
                ToolName = "ClearBrowserCache",
                Stage = 0,
                ArgumentsJson = "{}",
            };

            var result = executor.ExecuteAsync(call).GetAwaiter().GetResult();
            if (result.IsError)
                throw new InvalidOperationException($"ClearBrowserCache failed: {result.Content}");
            if (!control.WasCalled)
                throw new InvalidOperationException("ClearBrowserCacheAsync was not invoked.");

            using var doc = JsonDocument.Parse(result.Content);
            if (doc.RootElement.GetProperty("windows").GetInt32() != 2
                || doc.RootElement.GetProperty("tabsReloaded").GetInt32() != 3)
            {
                throw new InvalidOperationException($"Unexpected ClearBrowserCache payload: {result.Content}");
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

    private static void AssertNullControlUnavailable()
    {
        var result = DysonNullBrowserControl.Instance
            .ClearBrowserCacheAsync()
            .GetAwaiter()
            .GetResult();
        if (!result.IsError
            || !result.Error.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Null control must return unavailable for ClearBrowserCache.");
        }
    }

    private sealed class RecordingBrowserControl(int windows, int tabsReloaded) : IDysonBrowserControl
    {
        public bool WasCalled { get; private set; }

#pragma warning disable CS0067
        public event Action<DysonBrowserSnipPayload>? SnipCaptured;
#pragma warning restore CS0067

        public Task<Result<IDysonBrowserWindow, string>> OpenBrowserAsync(
            string? url = null,
            int? width = null,
            int? height = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IDysonBrowserWindow, string>.AsError("not used"));

        public Task<Result<IReadOnlyList<IDysonBrowserWindow>, string>> ListWindowsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<IDysonBrowserWindow>, string>.AsValue([]));

        public Task<Result<IDysonBrowserWindow, string>> GetWindowAsync(
            string windowId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IDysonBrowserWindow, string>.AsError("not used"));

        public Task<Result<DysonBrowserCacheClearResult, string>> ClearBrowserCacheAsync(
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(Result<DysonBrowserCacheClearResult, string>.AsValue(
                new DysonBrowserCacheClearResult
                {
                    Windows = windows,
                    TabsReloaded = tabsReloaded,
                }));
        }
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
