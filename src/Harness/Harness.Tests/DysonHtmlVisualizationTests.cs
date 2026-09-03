using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

public class DysonHtmlVisualizationTests
{
    [Fact]
    public async Task Run()
    {
        AssertCatalogAndThemeGuidance();
        AssertLiveThemeInterpolation();
        await AssertCreateFileAndRenderExecutor();
        AssertVisualizationPersistence();
    }

    private static void AssertCatalogAndThemeGuidance()
    {
        var snapshot = new DysonUiThemeSnapshot("LIGHT", "#A1B2C3");
        var pipeline = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess, uiTheme: snapshot);
        if (pipeline.Tools.ContainsKey("CreateTempFile")
            || !pipeline.Tools.TryGetValue("CreateFile", out var createFile)
            || !pipeline.Tools.TryGetValue("RenderHtmlVisualization", out var render)
            || !createFile.Description.Contains("Use that returned path verbatim", StringComparison.Ordinal)
            || !createFile.InputSchemaJson.Contains("isTempFile", StringComparison.Ordinal)
            || !render.Description.Contains("highly encouraged", StringComparison.Ordinal)
            || !render.Description.Contains("light theme with accent color #a1b2c3", StringComparison.Ordinal)
            || !render.Description.Contains("later harness stage", StringComparison.Ordinal)
            || !render.Description.Contains("network requests", StringComparison.Ordinal)
            || render.Description.Contains("{theme}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("HTML visualization catalog description/schema mismatch.");
        }

        var formatted = DysonAgentSystemPrompts.FormatVisualizationThemeGuidance(snapshot);
        if (!formatted.Contains("light theme with accent color #a1b2c3", StringComparison.Ordinal))
            throw new InvalidOperationException("Theme guidance formatter mismatch.");
    }

    private static void AssertLiveThemeInterpolation()
    {
        var initial = new DysonUiThemeSnapshot("light", "#a1b2c3");
        var session = new StubSession(new DysonAgentSessionConfig { UiTheme = initial });
        if (!session.McpPipeline.Tools.TryGetValue("RenderHtmlVisualization", out var first)
            || !first.Description.Contains("light theme with accent color #a1b2c3", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Initial visualization theme guidance mismatch.");
        }

        var updated = new DysonUiThemeSnapshot("dark", "#3dbf7a");
        session.ApplyUiTheme(updated);
        if (!session.McpPipeline.Tools.TryGetValue("RenderHtmlVisualization", out var second)
            || !second.Description.Contains("dark theme with accent color #3dbf7a", StringComparison.Ordinal)
            || second.Description.Contains("#a1b2c3", StringComparison.Ordinal)
            || second.Description.Contains("{theme}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ApplyUiTheme did not replace visualization theme guidance.");
        }

        var before = second.Description;
        session.ApplyUiTheme(updated);
        if (!session.McpPipeline.Tools.TryGetValue("RenderHtmlVisualization", out var again)
            || !string.Equals(before, again.Description, StringComparison.Ordinal)
            || session.SystemPromptGeneration != 0)
        {
            throw new InvalidOperationException(
                "Identical ApplyUiTheme must keep description bytes and SystemPromptGeneration.");
        }

        var applied = session.ApplyAgentMode(DysonAgentModes.Plan);
        if (applied.IsError)
            throw new InvalidOperationException("ApplyAgentMode(Plan) failed: " + applied.Error);
        if (!session.McpPipeline.Tools.TryGetValue("RenderHtmlVisualization", out var afterPlan)
            || !afterPlan.Description.Contains("#3dbf7a", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Mode rebuild must keep the live theme snapshot.");
        }

        var pipeline = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        pipeline.Tools.Remove("RenderHtmlVisualization");
        pipeline.ApplyVisualizationTheme(updated);
        if (pipeline.Tools.ContainsKey("RenderHtmlVisualization"))
            throw new InvalidOperationException("ApplyVisualizationTheme must no-op when the tool is omitted.");
    }

    private static async Task AssertCreateFileAndRenderExecutor()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-html-visualization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var session = new StubSession(new DysonAgentSessionConfig());
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, root, new HttpClient());

            var normal = await ExecuteAsync(executor, "CreateFile", "{\"path\":\"normal.txt\",\"content\":\"normal\"}");
            if (normal.IsError || normal.Content != "Created normal.txt (6 chars).")
                throw new InvalidOperationException("Normal CreateFile behavior must remain unchanged.");

            var temporary = await ExecuteAsync(executor, "CreateFile", "{\"path\":\"chart.html\",\"content\":\"<main>from-file</main>\",\"isTempFile\":true}");
            if (temporary.IsError)
                throw new InvalidOperationException("Temp CreateFile failed: " + temporary.Content);

            using var tempAcknowledgement = JsonDocument.Parse(temporary.Content);
            var tempPath = tempAcknowledgement.RootElement.GetProperty("path").GetString()!;
            if (!tempPath.StartsWith(".dyson/temp/chart-", StringComparison.Ordinal)
                || !tempPath.EndsWith(".html", StringComparison.Ordinal)
                || !File.Exists(Path.Combine(root, tempPath.Replace('/', Path.DirectorySeparatorChar))))
            {
                throw new InvalidOperationException("Temporary CreateFile must return its exact generated relative path.");
            }

            var invalidTemp = await ExecuteAsync(executor, "CreateFile", "{\"path\":\"dir/chart.html\",\"content\":\"x\",\"isTempFile\":true}");
            if (!invalidTemp.IsError)
                throw new InvalidOperationException("Temp CreateFile must reject directory components.");

            var rendered = await ExecuteAsync(executor, "RenderHtmlVisualization", $$"""
                {
                  "title":"Quarterly revenue",
                  "html":{"tempFile":"{{tempPath}}"},
                  "css":{"content":""},
                  "js":{"content":""}
                }
                """);
            if (rendered.IsError || rendered.HtmlVisualization is null
                || rendered.HtmlVisualization.Html != "<main>from-file</main>"
                || !rendered.Content.Contains("\"rendered\":true", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("RenderHtmlVisualization must attach a typed visualization and short acknowledgement.");
            }

            var rejected = await ExecuteAsync(executor, "RenderHtmlVisualization", """
                {"title":"bad","html":{"content":"<main>x</main>","tempFile":".dyson/temp/nope.html"},"css":{"content":""},"js":{"content":""}}
                """);
            if (!rejected.IsError)
                throw new InvalidOperationException("RenderHtmlVisualization must reject ambiguous asset sources.");
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

    private static void AssertVisualizationPersistence()
    {
        var visualization = new DysonHtmlVisualization
        {
            Id = Guid.NewGuid(),
            Title = "Persisted",
            Html = "<p>x</p>",
            Css = "p{}",
            JavaScript = "",
        };
        var result = new DysonToolCallResult
        {
            CallId = "render-1",
            ToolName = "RenderHtmlVisualization",
            Stage = 1,
            Content = "{\"rendered\":true}",
            BinaryAttachment = new DysonBinaryAttachment
            {
                FileName = "x.png",
                Extension = ".png",
                MimeType = "image/png",
                Base64Data = "AA==",
            },
            HtmlVisualization = visualization,
        };
        var json = DysonTurnToolStateSerializer.Serialize(new DysonTurnToolState { ResponseLog = [result] });
        var restored = DysonTurnToolStateSerializer.Deserialize(json).ResponseLog.Single();
        if (restored.HtmlVisualization?.Id != visualization.Id
            || result.WithoutBinaryAttachment().HtmlVisualization?.Id != visualization.Id)
        {
            throw new InvalidOperationException("Visualization payload must survive persistence and binary stripping.");
        }
    }

    private static Task<DysonToolCallResult> ExecuteAsync(
        DysonWorkspaceToolExecutor executor,
        string toolName,
        string argumentsJson) =>
        executor.ExecuteAsync(new DysonToolCall
        {
            CallId = Guid.NewGuid().ToString("N"),
            ToolName = toolName,
            Stage = 1,
            ArgumentsJson = argumentsJson,
        });

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(DysonAgentSessionConfig config) : DysonAgentSession(
        DysonAgentModes.Explore,
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
