using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Voice sidecar work-directory tools: same executor bodies and owner gate as the Meta Agent,
/// keyed on the sidecar's own work-directory id (its Meta's).
/// </summary>
public class DysonVoiceSidecarWorkdirTests
{
    [Fact]
    public async Task Sidecar_on_screen_lists_and_switches_and_tells_its_meta()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var work = new DysonWorkDirectoryFocusTests.FakeWorkDirectories();
        work.Add(new DysonWorkDirectoryEntity { Id = a, Name = "DysonHarness", AbsolutePath = "E:\\DH" });
        work.Add(new DysonWorkDirectoryEntity { Id = b, Name = "RealStrategy", AbsolutePath = "E:\\RS" });
        var focus = new DysonWorkDirectoryFocus(work);
        focus.SetActive(a);
        var hooked = 0;
        focus.SwitchTo = id =>
        {
            hooked++;
            focus.SetActive(id);
            return Task.CompletedTask;
        };

        var owner = new StubSession(DysonAgentModes.MetaAgent);
        var sidecar = new StubSession(DysonAgentModes.VoiceSidecar, new DysonAgentSessionConfig { Focus = focus });
        sidecar.VoiceSidecarOwner = owner;
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(
            sidecar, Path.GetTempPath(), http, workDirectoryId: a);

        var list = await executor.ExecuteAsync(Call("ListWorkdirs", "{}"));
        Assert.False(list.IsError, list.Content);
        Assert.Contains("RealStrategy", list.Content, StringComparison.Ordinal);

        var switched = await executor.ExecuteAsync(Call("SwitchWorkdir", $$"""{"id":"{{b}}"}"""));
        Assert.False(switched.IsError, switched.Content);
        Assert.Equal(1, hooked);
        Assert.Equal(b, focus.ActiveId);
        Assert.Contains(
            owner.Turns,
            t => t.Kind == DysonAgentTurnKind.DisplayInfo
                && t.AssistantText == "[voice] switched the screen to RealStrategy");

        // Now off screen: the same gate as a background Meta, no hook call.
        var deniedList = await executor.ExecuteAsync(Call("ListWorkdirs", "{}"));
        var deniedSwitch = await executor.ExecuteAsync(Call("SwitchWorkdir", $$"""{"id":"{{a}}"}"""));
        Assert.True(deniedList.IsError);
        Assert.Contains(DysonWorkDirectoryFocus.NotOwnerMessage, deniedList.Content, StringComparison.Ordinal);
        Assert.Contains(DysonWorkDirectoryFocus.NotOwnerMessage, deniedSwitch.Content, StringComparison.Ordinal);
        Assert.Equal(1, hooked);
        Assert.Equal(b, focus.ActiveId);
    }

    [Fact]
    public async Task Work_session_cannot_switch_and_sidecar_prompt_names_the_tools()
    {
        var focus = new DysonWorkDirectoryFocus(new DysonWorkDirectoryFocusTests.FakeWorkDirectories());
        var wd = Guid.NewGuid();
        focus.SetActive(wd);
        var work = new StubSession(DysonAgentModes.Work, new DysonAgentSessionConfig { Focus = focus });
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(
            work, Path.GetTempPath(), http, workDirectoryId: wd);

        var refused = await executor.ExecuteAsync(Call("SwitchWorkdir", $$"""{"id":"{{Guid.NewGuid()}}"}"""));
        Assert.True(refused.IsError);
        Assert.Equal(wd, focus.ActiveId);

        Assert.Contains("ListWorkdirs", DysonAgentSystemPrompts.VoiceModeMetaSidecarDirective, StringComparison.Ordinal);
        Assert.Contains("SwitchWorkdir", DysonAgentSystemPrompts.VoiceModeMetaSidecarDirective, StringComparison.Ordinal);
    }

    private static DysonToolCall Call(string name, string args) => new()
    {
        CallId = Guid.NewGuid().ToString("N"),
        ToolName = name,
        Stage = 0,
        ArgumentsJson = args,
    };

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(
        string mode,
        DysonAgentSessionConfig? config = null) : DysonAgentSession(
        mode,
        config ?? new DysonAgentSessionConfig(),
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
