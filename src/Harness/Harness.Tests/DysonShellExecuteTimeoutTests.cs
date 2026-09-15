using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: MCP ShellExecute must reject omitted/&lt;=0 timeoutMs before starting a process (Xunit Fact).
/// </summary>
public class DysonShellExecuteTimeoutTests
{
    [Fact]
    public async Task Run()
    {
        AssertSchemaRequiresTimeoutMs();
        await AssertExecutorRejectsMissingOrNonPositiveTimeout();
    }

    private static void AssertSchemaRequiresTimeoutMs()
    {
        var tool = DysonMcpPipeline.CreateShellExecuteTool(["PowerShell"]);
        if (tool is null)
            throw new InvalidOperationException("CreateShellExecuteTool must return a tool when shells are available.");

        if (!tool.InputSchemaJson.Contains("\"timeoutMs\"", StringComparison.Ordinal)
            || !tool.InputSchemaJson.Contains(
                "\"required\": [\"shell\", \"command\", \"timeoutMs\"]",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "ShellExecute schema must require shell, command, and timeoutMs. Schema:\n" + tool.InputSchemaJson);
        }

        if (!tool.Description.Contains("timeoutMs (integer > 0) is required", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "ShellExecute description must require timeoutMs. Description:\n" + tool.Description);
        }
    }

    private static async Task AssertExecutorRejectsMissingOrNonPositiveTimeout()
    {
        var session = new StubSession();
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, Path.GetTempPath(), http);
        foreach (var args in new[]
                 {
                     """{"shell":"PowerShell","command":"echo hi"}""",
                     """{"shell":"PowerShell","command":"echo hi","timeoutMs":0}""",
                     """{"shell":"PowerShell","command":"echo hi","timeoutMs":-1}""",
                 })
        {
            var result = await executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "shell-timeout",
                ToolName = "ShellExecute",
                Stage = 0,
                ArgumentsJson = args,
            });
            if (!result.IsError
                || !result.Content.Contains(
                    "ShellExecute: timeoutMs (integer > 0) is required.",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected timeoutMs required for {args}. Got:\n" + result.Content);
            }
        }
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig
        {
            AvailableShells = [new DysonConfiguredShellSpec("PowerShell", "powershell.exe")],
        },
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
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
