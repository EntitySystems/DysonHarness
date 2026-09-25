using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// LoadSkill in Meta Agent mode rejects Literal (file-path) results and still loads named skills.
/// </summary>
public class DysonMetaAgentLoadSkillTests
{
    [Fact]
    public async Task Literal_path_is_rejected_in_meta_agent_and_succeeds_in_work()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-meta-skill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "docs", "ui"));
        var relative = Path.Combine("docs", "ui", "README.md");
        File.WriteAllText(Path.Combine(root, relative), "# UI docs");

        try
        {
            using var http = new HttpClient();
            var meta = new StubSession(DysonAgentModes.MetaAgent);
            var metaExecutor = await DysonWorkspaceTestFs.CreateExecutorAsync(meta, root, http);
            var rejected = await metaExecutor.ExecuteAsync(new DysonToolCall
            {
                CallId = "1",
                ToolName = "LoadSkill",
                Stage = 0,
                ArgumentsJson = """{"name":"docs/ui/README.md","loadIndexOnly":true}""",
            });
            Assert.True(rejected.IsError);
            Assert.Equal(DysonMetaAgentTools.LoadSkillLiteralRejectedMessage, rejected.Content);

            var included = await metaExecutor.ExecuteAsync(new DysonToolCall
            {
                CallId = "2",
                ToolName = "LoadSkill",
                Stage = 0,
                ArgumentsJson = """{"name":"JDSL","loadIndexOnly":true}""",
            });
            Assert.False(included.IsError);
            Assert.Contains("JsonDynamicStructuredLanguageToolchain", included.Content, StringComparison.Ordinal);

            var work = new StubSession(DysonAgentModes.Work);
            var workExecutor = await DysonWorkspaceTestFs.CreateExecutorAsync(work, root, http);
            var allowed = await workExecutor.ExecuteAsync(new DysonToolCall
            {
                CallId = "3",
                ToolName = "LoadSkill",
                Stage = 0,
                ArgumentsJson = """{"name":"docs/ui/README.md","loadIndexOnly":true}""",
            });
            Assert.False(allowed.IsError);
            Assert.Contains("# UI docs", allowed.Content, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode) : DysonAgentSession(
        mode,
        new DysonAgentSessionConfig(),
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
