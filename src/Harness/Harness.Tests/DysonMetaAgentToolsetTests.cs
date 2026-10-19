using System.Runtime.CompilerServices;
using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>Meta Agent catalog is the allowlist; Meta Agent Drone keeps TriggerParentEvent and drops Ask/dialog FromParent tools.</summary>
public class DysonMetaAgentToolsetTests
{
    [Fact]
    public void Meta_agent_catalog_is_exactly_the_allowlist()
    {
        var pipeline = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.MetaAgent);
        var names = pipeline.Tools.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var expected = DysonMetaAgentTools.AllowedToolNames.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, names);
    }

    [Fact]
    public void Meta_agent_catalog_keeps_browser_tools_when_browser_control_is_set()
    {
        var pipeline = DysonSessionToolsetBuilder.Build(
            new DysonAgentSessionConfig { BrowserControl = new DysonNullBrowserControl() },
            DysonAgentModes.MetaAgent);

        foreach (var tool in DysonMcpPipeline.CreateBrowserTools())
            Assert.Contains(tool.Name, pipeline.Tools.Keys);

        foreach (var name in new[]
        {
            "ReadFile", "WriteFile", "Grep", "ListDirectory",
            "CreateFile", "CreateDirectory", "ShellExecute", "LoadBinary",
        })
            Assert.DoesNotContain(name, pipeline.Tools.Keys);
    }

    [Fact]
    public void Meta_agent_drone_catalog_keeps_trigger_parent_event_and_adds_plan_tools()
    {
        var pipeline = DysonSessionToolsetBuilder.Build(
            new DysonAgentSessionConfig(),
            DysonAgentModes.MetaAgentDrone,
            interAgentDepth: 1,
            omitRootTaskCompletionTools: true);

        Assert.False(pipeline.Tools.ContainsKey("AskQuestionFromParent"));
        Assert.False(pipeline.Tools.ContainsKey("PromptUserDialogFromParent"));
        Assert.True(pipeline.Tools.ContainsKey("TriggerParentEvent"));
        Assert.True(pipeline.Tools.ContainsKey("ReadMetaPlan"));
        Assert.True(pipeline.Tools.ContainsKey("EditMetaPlan"));
        Assert.True(pipeline.Tools.ContainsKey("SubmitMetaPlan"));
        Assert.True(pipeline.Tools.ContainsKey("ReadFile"));
        Assert.True(pipeline.Tools.ContainsKey("SubmitSubagentReport"));
        Assert.False(pipeline.Tools.ContainsKey("WriteTempFile"));
        Assert.False(pipeline.Tools.ContainsKey("ReadTempFile"));
        Assert.False(pipeline.Tools.ContainsKey("PostUserQuestion"));
    }

    [Fact]
    public void Child_roster_with_reports_is_byte_identical_across_calls()
    {
        var parent = new StubSession(DysonAgentModes.MetaAgent);
        var drone = new StubSession(DysonAgentModes.MetaAgentDrone);
        drone.WorktreeBranch = "dyson/abcd1234";
        parent.RegisterForTest(drone);

        var first = parent.FormatChildRosterJson(includeReports: true);
        var second = parent.FormatChildRosterJson(includeReports: true);
        Assert.Equal(first, second);

        using var doc = JsonDocument.Parse(first);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        var item = doc.RootElement[0];
        Assert.Equal("drone", item.GetProperty("kind").GetString());
        Assert.Equal("dyson/abcd1234", item.GetProperty("worktreeBranch").GetString());
        Assert.True(item.TryGetProperty("lastReport", out _));
        Assert.True(item.TryGetProperty("finishedAt", out _));
        Assert.False(item.TryGetProperty("notice", out _));
    }

    [Fact]
    public void List_subagents_json_does_not_grow_report_fields()
    {
        var parent = new StubSession(DysonAgentModes.Work);
        var child = new StubSession(DysonAgentModes.Explore);
        parent.RegisterForTest(child);

        using var doc = JsonDocument.Parse(parent.FormatListSubagentsJson());
        var item = doc.RootElement[0];
        Assert.False(item.TryGetProperty("kind", out _));
        Assert.False(item.TryGetProperty("lastReport", out _));
        Assert.False(item.TryGetProperty("finishedAt", out _));
        Assert.False(item.TryGetProperty("worktreeBranch", out _));
    }

    [Fact]
    public void Workdir_tools_only_on_root_meta_catalog()
    {
        var voice = (DysonVoiceMode)RuntimeHelpers.GetUninitializedObject(typeof(DysonVoiceMode));
        foreach (var config in new DysonAgentSessionConfig[]
        {
            new(),
            new() { Voice = voice },
        })
        {
            var meta = DysonSessionToolsetBuilder.Build(config, DysonAgentModes.MetaAgent);
            Assert.Contains("ListWorkdirs", meta.Tools.Keys);
            Assert.Contains("OpenWorkdirsPanel", meta.Tools.Keys);
            Assert.Contains("SwitchWorkdir", meta.Tools.Keys);
        }

        foreach (var mode in new[]
        {
            DysonAgentModes.MetaAgentDrone,
            DysonAgentModes.Drone,
            DysonAgentModes.Work,
            DysonAgentModes.Explore,
        })
        {
            var pipeline = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), mode);
            Assert.DoesNotContain("ListWorkdirs", pipeline.Tools.Keys);
            Assert.DoesNotContain("OpenWorkdirsPanel", pipeline.Tools.Keys);
            Assert.DoesNotContain("SwitchWorkdir", pipeline.Tools.Keys);
        }
    }

    [Fact]
    public void SpeakToUser_OnlyOnRootMetaAgent_WhenVoiceOn()
    {
        var voice = (DysonVoiceMode)RuntimeHelpers.GetUninitializedObject(typeof(DysonVoiceMode));
        var voiced = new DysonAgentSessionConfig { Voice = voice };

        var on = DysonSessionToolsetBuilder.Build(voiced, DysonAgentModes.MetaAgent);
        Assert.True(on.Tools.ContainsKey("SpeakToUser"));
        foreach (var name in DysonMetaAgentTools.AllowedToolNames)
            Assert.Contains(name, on.Tools.Keys);

        var off = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.MetaAgent);
        Assert.False(off.Tools.ContainsKey("SpeakToUser"));

        foreach (var mode in new[]
        {
            DysonAgentModes.MetaAgentDrone,
            DysonAgentModes.Drone,
            DysonAgentModes.Explore,
            DysonAgentModes.BugReview,
            DysonAgentModes.SecurityReview,
            DysonAgentModes.Ask,
            DysonAgentModes.Plan,
            DysonAgentModes.Work,
        })
        {
            var pipeline = DysonSessionToolsetBuilder.Build(voiced, mode);
            Assert.False(pipeline.Tools.ContainsKey("SpeakToUser"), mode);
        }
    }

    [Fact]
    public void SetVoiceMode_RootMetaAgent_RebuildsPromptAndCatalog()
    {
        var voice = (DysonVoiceMode)RuntimeHelpers.GetUninitializedObject(typeof(DysonVoiceMode));
        var session = new StubSession(DysonAgentModes.MetaAgent, "models-block");
        var turnedOn = session.SetVoiceMode(voice);
        Assert.True(turnedOn.IsSuccess);
        Assert.Contains("SpeakToUser", session.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("[voice input]", session.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("models-block", session.SystemPrompt, StringComparison.Ordinal);
        Assert.True(session.McpPipeline.Tools.ContainsKey("SpeakToUser"));
        Assert.Equal(1, session.SystemPromptGeneration);

        var same = session.SetVoiceMode(voice);
        Assert.True(same.IsSuccess);
        Assert.Equal(1, session.SystemPromptGeneration);

        var turnedOff = session.SetVoiceMode(null);
        Assert.True(turnedOff.IsSuccess);
        Assert.False(session.McpPipeline.Tools.ContainsKey("SpeakToUser"));
        Assert.DoesNotContain("Voice mode (on):", session.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("models-block", session.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(2, session.SystemPromptGeneration);

        var work = new StubSession(DysonAgentModes.Work);
        var denied = work.SetVoiceMode(voice);
        Assert.True(denied.IsError);
        Assert.Contains("root Meta Agent", denied.Error, StringComparison.Ordinal);

        var parent = new StubSession(DysonAgentModes.Work);
        var child = new StubSession(DysonAgentModes.MetaAgent);
        parent.RegisterForTest(child);
        Assert.True(child.SetVoiceMode(voice).IsError);
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode, string? suffix = null) : DysonAgentSession(
        mode,
        new DysonAgentSessionConfig(),
        new StubProvider(),
        suffix)
    {
        public void RegisterForTest(DysonAgentSession child) => RegisterSubagent(child);

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
