using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Mode tool denylist: empty policy keeps tools; omit + mode-switch rebuild; resolver ignores models.
/// </summary>
public class DysonToolPolicyTests
{
    [Fact]
    public void Run()
    {
        AssertEmptyPolicyKeepsTools();
        AssertDisabledToolsOmitted();
        AssertModeSwitchRebuildRestoresTools();
        AssertResolverIgnoresModelOverlays();
        AssertStructuralGateStillWins();
    }

    private static void AssertEmptyPolicyKeepsTools()
    {
        var config = new DysonAgentSessionConfig
        {
            AvailableShells = DysonShell.DefaultConfiguredShellsForCurrentPlatform(),
        };
        var pipeline = DysonSessionToolsetBuilder.Build(config, DysonAgentModes.Work);
        if (!pipeline.Tools.ContainsKey("WriteFile"))
            throw new InvalidOperationException("Empty policy should keep default catalog tools.");
        if (config.AvailableShells.Count > 0 && !pipeline.Tools.ContainsKey("ShellExecute"))
            throw new InvalidOperationException("Empty policy should keep ShellExecute when shells are configured.");
    }

    private static void AssertDisabledToolsOmitted()
    {
        var config = new DysonAgentSessionConfig
        {
            AvailableShells = DysonShell.DefaultConfiguredShellsForCurrentPlatform(),
            DisabledTools = new HashSet<string>(StringComparer.Ordinal) { "WriteFile", "ShellExecute" },
        };
        var pipeline = DysonSessionToolsetBuilder.Build(config, DysonAgentModes.Ask);
        if (pipeline.Tools.ContainsKey("WriteFile") || pipeline.Tools.ContainsKey("ShellExecute"))
            throw new InvalidOperationException("Disabled tools should be omitted from the catalog.");
        if (!pipeline.Tools.ContainsKey("ReadFile"))
            throw new InvalidOperationException("Unrelated tools should remain.");
    }

    private static void AssertModeSwitchRebuildRestoresTools()
    {
        var doc = new DysonToolPolicyDocument
        {
            Modes =
            {
                [DysonAgentModes.Ask] = new DysonToolPolicyModeEntry
                {
                    DisabledTools = ["WriteFile"],
                },
                [DysonAgentModes.Work] = new DysonToolPolicyModeEntry
                {
                    DisabledTools = [],
                },
            },
        };

        var config = new DysonAgentSessionConfig { ToolPolicy = doc };
        var session = new StubSession(DysonAgentModes.Ask, config);
        session.ConfigureRootForTest();

        if (session.McpPipeline.Tools.ContainsKey("WriteFile"))
            throw new InvalidOperationException("Ask mode should omit WriteFile.");

        var applied = session.ApplyAgentMode(DysonAgentModes.Work);
        if (applied.IsError)
            throw new InvalidOperationException(applied.Error);

        if (!session.McpPipeline.Tools.ContainsKey("WriteFile"))
            throw new InvalidOperationException("Work mode rebuild should restore WriteFile.");
        if (!session.McpPipeline.Tools.ContainsKey("AskQuestion"))
            throw new InvalidOperationException("Root depth-0 tools should remain after rebuild.");
    }

    private static void AssertResolverIgnoresModelOverlays()
    {
        var slugId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var doc = new DysonToolPolicyDocument
        {
            Modes =
            {
                [DysonAgentModes.Work] = new DysonToolPolicyModeEntry
                {
                    DisabledTools = ["ReadFile"],
                },
            },
            Models =
            {
                [slugId.ToString("D")] = new DysonToolPolicyModelEntry
                {
                    Modes =
                    {
                        [DysonAgentModes.Work] = new DysonToolPolicyModeEntry
                        {
                            DisabledTools = ["StartSubagent", "WriteFile"],
                        },
                    },
                },
            },
        };

        var resolved = DysonToolPolicyResolver.Resolve(doc, DysonAgentModes.Work, slugId);
        if (!resolved.Contains("ReadFile"))
            throw new InvalidOperationException("Mode denylist should apply.");
        if (resolved.Contains("StartSubagent") || resolved.Contains("WriteFile"))
            throw new InvalidOperationException("Model overlay must be ignored in v1.");
    }

    private static void AssertStructuralGateStillWins()
    {
        var config = new DysonAgentSessionConfig
        {
            AvailableShells = [],
            DisabledTools = new HashSet<string>(StringComparer.Ordinal),
        };
        var pipeline = DysonSessionToolsetBuilder.Build(config, DysonAgentModes.Work);
        if (pipeline.Tools.ContainsKey("ShellExecute"))
            throw new InvalidOperationException("Structural shell gate should omit ShellExecute.");
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode, DysonAgentSessionConfig config) : DysonAgentSession(
        mode,
        config,
        new StubProvider())
    {
        public void ConfigureRootForTest() => ConfigureRootInterAgentTools();

        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
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
