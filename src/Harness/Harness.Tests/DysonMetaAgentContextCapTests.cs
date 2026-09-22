using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: Meta Agent MaxTargetContextTokens is pinned at 100K; drones keep the cascade.
/// </summary>
public class DysonMetaAgentContextCapTests
{
    [Fact]
    public void Meta_agent_resolves_to_100K_even_when_configured_otherwise()
    {
        var meta = new StubSession(DysonAgentModes.MetaAgent)
        {
            MaxTargetContextTokens = 400_000,
            SlugDefaultMaxTargetContextTokens = 250_000,
        };
        Assert.Equal(DysonMaxTargetContextTokens.HarnessDefault, meta.ResolveEffectiveMaxTargetContextTokens());

        meta.MaxTargetContextTokens = 0;
        Assert.Equal(DysonMaxTargetContextTokens.HarnessDefault, meta.ResolveEffectiveMaxTargetContextTokens());

        meta.MaxTargetContextTokens = null;
        meta.SlugDefaultMaxTargetContextTokens = null;
        Assert.Equal(DysonMaxTargetContextTokens.HarnessDefault, meta.ResolveEffectiveMaxTargetContextTokens());
    }

    [Fact]
    public void Work_and_meta_agent_drone_keep_the_normal_cascade()
    {
        var work = new StubSession(DysonAgentModes.Work)
        {
            MaxTargetContextTokens = 400_000,
            SlugDefaultMaxTargetContextTokens = 250_000,
        };
        Assert.Equal(400_000, work.ResolveEffectiveMaxTargetContextTokens());

        var drone = new StubSession(DysonAgentModes.MetaAgentDrone)
        {
            MaxTargetContextTokens = 400_000,
            SlugDefaultMaxTargetContextTokens = 250_000,
        };
        Assert.Equal(400_000, drone.ResolveEffectiveMaxTargetContextTokens());

        drone.MaxTargetContextTokens = 0;
        Assert.Equal(0, drone.ResolveEffectiveMaxTargetContextTokens());
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
