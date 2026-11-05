using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ShellExecute publishes <see cref="DysonShellVcsCommandRanEvent"/> on the work-directory scope
/// after a gh/git command exits. The hook has no pipeline service or registry dependency.
/// </summary>
public class DysonShellVcsHookTests
{
    [Fact]
    public async Task Git_command_publishes_exactly_one_event_on_the_workdir_scope()
    {
        var workDirectoryId = Guid.NewGuid();
        using var bus = new DysonMessageBus();
        var hits = new List<DysonShellVcsCommandRanEvent>();
        var subscribed = bus.Subscribe<DysonShellVcsCommandRanEvent>(
            DysonBusScopes.WorkDirectory(workDirectoryId),
            hits.Add);
        Assert.False(subscribed.IsError, subscribed.Error);
        using var _ = subscribed.Value;

        var result = await RunAsync(bus, workDirectoryId, "git --version");

        Assert.False(result.IsError, result.Content);
        var evt = Assert.Single(hits);
        Assert.Equal(workDirectoryId, evt.WorkDirectoryId);
    }

    [Fact]
    public async Task Non_vcs_command_publishes_nothing()
    {
        var workDirectoryId = Guid.NewGuid();
        using var bus = new DysonMessageBus();
        var hits = new List<DysonShellVcsCommandRanEvent>();
        var subscribed = bus.Subscribe<DysonShellVcsCommandRanEvent>(
            DysonBusScopes.WorkDirectory(workDirectoryId),
            hits.Add);
        Assert.False(subscribed.IsError, subscribed.Error);
        using var _ = subscribed.Value;

        var result = await RunAsync(bus, workDirectoryId, "echo hi");

        Assert.False(result.IsError, result.Content);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Empty_work_directory_id_publishes_nothing()
    {
        using var bus = new DysonMessageBus();
        var hits = new List<DysonShellVcsCommandRanEvent>();
        var subscribed = bus.Subscribe<DysonShellVcsCommandRanEvent>(
            DysonBusScopes.WorkDirectory(Guid.Empty),
            hits.Add);
        Assert.False(subscribed.IsError, subscribed.Error);
        using var _ = subscribed.Value;

        var result = await RunAsync(bus, Guid.Empty, "git --version");

        Assert.False(result.IsError, result.Content);
        Assert.Empty(hits);
    }

    private static async Task<DysonToolCallResult> RunAsync(DysonMessageBus bus, Guid workDirectoryId, string command)
    {
        var session = new StubSession();
        session.Config.Bus = bus;
        using var http = new HttpClient();
        var root = Path.Combine(Path.GetTempPath(), "dyson-vcs-hook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, root, http, workDirectoryId: workDirectoryId);
            return await executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "shell-vcs",
                ToolName = "ShellExecute",
                Stage = 0,
                ArgumentsJson = "{\"shell\":\"" + Shell.Name + "\",\"command\":" + System.Text.Json.JsonSerializer.Serialize(command) + ",\"timeoutMs\":30000}",
            });
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Temp dir still locked by a just-exited shell; the OS cleans %TEMP%.
            }
        }
    }

    /// <summary>powershell.exe is Windows-only; Linux/macOS CI runs the same commands under sh.</summary>
    private static readonly DysonConfiguredShellSpec Shell = OperatingSystem.IsWindows()
        ? new DysonConfiguredShellSpec("PowerShell", "powershell.exe")
        : new DysonConfiguredShellSpec("Sh", "sh");

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig
        {
            AvailableShells = [Shell],
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
