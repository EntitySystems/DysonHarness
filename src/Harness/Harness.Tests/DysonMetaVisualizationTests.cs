using System.Text.Json;
using System.Text.RegularExpressions;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Meta Agent visualizations: temp-scoped read/write, render, and a posted visualization id.
/// </summary>
public class DysonMetaVisualizationTests
{
    [Fact]
    public async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-meta-viz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var session = new StubSession(DysonAgentModes.MetaAgent);
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, root, new HttpClient());

            const string html = "<main>from-temp</main>";
            var written = await ExecuteAsync(executor, "WriteTempFile", "{\"path\":\"chart.html\",\"content\":\"" + html + "\"}");
            Assert.False(written.IsError, written.Content);
            using (var ack = JsonDocument.Parse(written.Content))
            {
                var path = ack.RootElement.GetProperty("path").GetString();
                Assert.Matches(new Regex(@"^\.dyson/temp/chart-[0-9a-f]{24}\.html$"), path);
                Assert.True(ack.RootElement.GetProperty("isTempFile").GetBoolean());

                var read = await ExecuteAsync(executor, "ReadTempFile", "{\"path\":" + JsonSerializer.Serialize(path) + "}");
                Assert.False(read.IsError, read.Content);
                using var readDoc = JsonDocument.Parse(read.Content);
                Assert.Equal(html, readDoc.RootElement.GetProperty("content").GetString());

                var rendered = await ExecuteAsync(executor, "RenderHtmlVisualization", $$"""
                    {
                      "title":"From temp",
                      "html":{"tempFile":{{JsonSerializer.Serialize(path)}}},
                      "css":{"content":""},
                      "js":{"content":""}
                    }
                    """);
                Assert.False(rendered.IsError, rendered.Content);
                Assert.NotNull(rendered.HtmlVisualization);
                Assert.Contains("visualizationId", rendered.Content, StringComparison.Ordinal);

                var inline = await ExecuteAsync(executor, "RenderHtmlVisualization", """
                    {"title":"Inline","html":{"content":"<p>inline</p>"},"css":{"content":""},"js":{"content":""}}
                    """);
                Assert.False(inline.IsError, inline.Content);
                Assert.NotNull(inline.HtmlVisualization);

                var rejected = await ExecuteAsync(executor, "RenderHtmlVisualization", """
                    {"title":"bad","html":{"tempFile":".dyson/temp/nope.html"},"css":{"content":""},"js":{"content":""}}
                    """);
                Assert.True(rejected.IsError);
                Assert.Null(rejected.HtmlVisualization);

                await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "secret-bytes");
                foreach (var badPath in new[] { "AGENTS.md", ".dyson/temp/chart.html" })
                {
                    var badRead = await ExecuteAsync(
                        executor,
                        "ReadTempFile",
                        "{\"path\":" + JsonSerializer.Serialize(badPath) + "}");
                    Assert.True(badRead.IsError);
                    Assert.Equal("ReadTempFile: path must be an exact generated file under .dyson/temp/.", badRead.Content);
                    Assert.DoesNotContain("secret-bytes", badRead.Content, StringComparison.Ordinal);
                }

                var tempDir = Path.Combine(root, ".dyson", "temp");
                var before = Directory.GetFiles(tempDir).Length;
                var nested = await ExecuteAsync(executor, "WriteTempFile", "{\"path\":\"dir/chart.html\",\"content\":\"x\"}");
                Assert.True(nested.IsError);
                Assert.Equal(before, Directory.GetFiles(tempDir).Length);

                var huge = new string('a', (512 * 1024) + 1);
                var tooBig = await ExecuteAsync(
                    executor,
                    "WriteTempFile",
                    "{\"path\":\"big.html\",\"content\":" + JsonSerializer.Serialize(huge) + "}");
                Assert.True(tooBig.IsError);
                Assert.Equal(before, Directory.GetFiles(tempDir).Length);

                var renderTurn = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal };
                renderTurn.ToolCalls.Add(new DysonToolCall
                {
                    CallId = "render-1",
                    ToolName = "RenderHtmlVisualization",
                    Stage = 1,
                });
                renderTurn.PrepareTrackedCalls();
                renderTurn.TrackedToolCalls[0].SetCompleted(rendered);
                session.AddTurnForTest(renderTurn);

                var vizId = rendered.HtmlVisualization!.Id;
                var beforePost = session.Turns.Count;
                var posted = await ExecuteAsync(executor, "PostConversationMessage", $$"""
                    {
                      "message":"see this",
                      "visualizationId":"{{vizId}}",
                      "actions":[{"name":"Go","func":"go"}]
                    }
                    """);
                Assert.False(posted.IsError, posted.Content);
                Assert.Equal("""{"ok":true}""", posted.Content);
                Assert.False(posted.EndsCurrentTurn);
                var bubble = session.Turns[^1];
                Assert.Equal(DysonAgentTurnKind.DisplayInfo, bubble.Kind);
                Assert.Equal(vizId, bubble.VisualizationId);
                var action = Assert.Single(bubble.ConversationActions);
                Assert.Equal("Go", action.Name);
                Assert.Equal("go", action.FuncKey);

                var entity = DysonTurnPersistence.ToEntity(bubble, Guid.NewGuid(), sequence: 1);
                Assert.Equal(vizId, entity.VisualizationId);
                Assert.Contains("\"func\"", entity.ConversationActionsJson, StringComparison.Ordinal);
                Assert.DoesNotContain("visualizationId", entity.ConversationActionsJson, StringComparison.Ordinal);
                var roundTrip = DysonTurnPersistence.ToEntity(bubble, entity.SessionId, 1);
                Assert.Equal(entity.VisualizationId, roundTrip.VisualizationId);

                var omitted = await ExecuteAsync(executor, "PostConversationMessage", "{\"message\":\"plain\"}");
                Assert.False(omitted.IsError, omitted.Content);
                Assert.Null(session.Turns[^1].VisualizationId);

                var nulled = await ExecuteAsync(
                    executor,
                    "PostConversationMessage",
                    "{\"message\":\"also plain\",\"visualizationId\":null}");
                Assert.False(nulled.IsError, nulled.Content);
                Assert.Null(session.Turns[^1].VisualizationId);

                var count = session.Turns.Count;
                var nope = await ExecuteAsync(
                    executor,
                    "PostConversationMessage",
                    "{\"message\":\"nope\",\"visualizationId\":\"nope\"}");
                Assert.True(nope.IsError);
                Assert.Equal("PostConversationMessage: visualizationId must be a GUID.", nope.Content);
                Assert.Equal(count, session.Turns.Count);

                var unknown = await ExecuteAsync(
                    executor,
                    "PostConversationMessage",
                    "{\"message\":\"missing\",\"visualizationId\":\"" + Guid.NewGuid() + "\"}");
                Assert.True(unknown.IsError);
                Assert.Equal("PostConversationMessage: unknown visualizationId.", unknown.Content);
                Assert.Equal(count, session.Turns.Count);
                Assert.Equal(beforePost + 3, session.Turns.Count);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best-effort */ }
        }

        var workRoot = Path.Combine(Path.GetTempPath(), "dyson-meta-viz-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        try
        {
            var work = new StubSession(DysonAgentModes.Work);
            var workExecutor = await DysonWorkspaceTestFs.CreateExecutorAsync(work, workRoot, new HttpClient());
            var write = await ExecuteAsync(workExecutor, "WriteTempFile", "{\"path\":\"chart.html\",\"content\":\"x\"}");
            var read = await ExecuteAsync(workExecutor, "ReadTempFile", "{\"path\":\".dyson/temp/chart-0123456789abcdef01234567.html\"}");
            Assert.True(write.IsError);
            Assert.Equal("WriteTempFile is only available in Meta Agent mode.", write.Content);
            Assert.True(read.IsError);
            Assert.Equal("ReadTempFile is only available in Meta Agent mode.", read.Content);
            Assert.False(Directory.Exists(Path.Combine(workRoot, ".dyson")));
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); }
            catch { /* best-effort */ }
        }

        foreach (var mode in new[] { DysonAgentModes.Work, DysonAgentModes.Explore })
        {
            var pipeline = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), mode);
            Assert.False(pipeline.Tools.ContainsKey("WriteTempFile"));
            Assert.False(pipeline.Tools.ContainsKey("ReadTempFile"));
        }

        var drone = DysonSessionToolsetBuilder.Build(
            new DysonAgentSessionConfig(),
            DysonAgentModes.MetaAgentDrone,
            interAgentDepth: 1,
            omitRootTaskCompletionTools: true);
        Assert.False(drone.Tools.ContainsKey("WriteTempFile"));
        Assert.False(drone.Tools.ContainsKey("ReadTempFile"));

        var meta = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.MetaAgent);
        Assert.True(meta.Tools.ContainsKey("WriteTempFile"));
        Assert.True(meta.Tools.ContainsKey("ReadTempFile"));
        Assert.False(meta.Tools.ContainsKey("CreateFile"));
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

    private sealed class StubSession(string mode) : DysonAgentSession(
        mode,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
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
            => Task.FromResult(VoidResult<string>.Success);

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
            => Task.FromResult(VoidResult<string>.Success);

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
