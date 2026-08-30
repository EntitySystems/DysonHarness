using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: cached outgoing-context-token estimate (TranscriptGeneration bump sites, coalesced
/// off-thread refresh) — single [Fact] per DysonDropContextTests convention.
/// </summary>
public class DysonOutgoingContextTokensCacheTests
{
    [Fact]
    public async Task Run()
    {
        await AssertRefreshMatchesEstimateAsync();
        await AssertStreamingDeltaDoesNotBumpGenerationAsync();
        AssertAddTurnBumpsGeneration();
        await AssertConcurrentRefreshesCoalesceAsync();
        await AssertNoOpWhenGenerationUnchangedAsync();
    }

    private static async Task AssertRefreshMatchesEstimateAsync()
    {
        var session = new StubSession(DysonAgentModes.Work);
        session.AddTurnForTest(Normal("hello world"));

        if (session.CachedOutgoingContextTokens != 0)
            throw new InvalidOperationException("Cache must be 0 before the first compute.");

        var changed = await session.RefreshOutgoingContextTokensAsync();
        if (!changed)
            throw new InvalidOperationException("First refresh after a turn add must report a change.");

        if (session.CachedOutgoingContextTokens != session.EstimateOutgoingContextTokens())
        {
            throw new InvalidOperationException(
                "CachedOutgoingContextTokens must match EstimateOutgoingContextTokens() after refresh.");
        }
    }

    private static async Task AssertStreamingDeltaDoesNotBumpGenerationAsync()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var turn = Normal("streaming host turn");
        session.AddTurnForTest(turn);
        await session.RefreshOutgoingContextTokensAsync();

        var generationBefore = session.TranscriptGeneration;
        turn.AppendStreamingDelta("partial token ");
        turn.AppendStreamingDelta("more partial token");

        if (session.TranscriptGeneration != generationBefore)
            throw new InvalidOperationException("Streaming deltas must not bump TranscriptGeneration.");
    }

    private static void AssertAddTurnBumpsGeneration()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var before = session.TranscriptGeneration;
        session.AddTurnForTest(Normal("turn one"));
        if (session.TranscriptGeneration == before)
            throw new InvalidOperationException("Adding a turn must bump TranscriptGeneration.");

        var afterFirst = session.TranscriptGeneration;
        session.AddTurnForTest(Normal("turn two"));
        if (session.TranscriptGeneration == afterFirst)
            throw new InvalidOperationException("Adding a second turn must bump TranscriptGeneration again.");
    }

    private static async Task AssertConcurrentRefreshesCoalesceAsync()
    {
        var counter = new CountingTokenCounter();
        var session = new StubSession(DysonAgentModes.Work, counter);
        session.AddTurnForTest(Normal("concurrent refresh sample text"));

        var baselineCalls = counter.CallCount;
        session.EstimateOutgoingContextTokens();
        var expectedPerCompute = counter.CallCount - baselineCalls;
        if (expectedPerCompute <= 0)
            throw new InvalidOperationException("Test setup must exercise at least one token-counter call per compute.");

        counter.Reset();

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => session.RefreshOutgoingContextTokensAsync())
            .ToArray();
        await Task.WhenAll(tasks);

        if (counter.CallCount != expectedPerCompute)
        {
            throw new InvalidOperationException(
                $"Concurrent RefreshOutgoingContextTokensAsync calls must coalesce into one computation " +
                $"(expected {expectedPerCompute} token-counter calls, got {counter.CallCount}).");
        }

        if (session.CachedOutgoingContextTokens != session.EstimateOutgoingContextTokens())
            throw new InvalidOperationException("Coalesced refresh must still produce the correct cached value.");
    }

    private static async Task AssertNoOpWhenGenerationUnchangedAsync()
    {
        var session = new StubSession(DysonAgentModes.Work);
        session.AddTurnForTest(Normal("no-op check"));

        var changed = await session.RefreshOutgoingContextTokensAsync();
        if (!changed)
            throw new InvalidOperationException("First refresh must report a change.");

        var again = await session.RefreshOutgoingContextTokensAsync();
        if (again)
            throw new InvalidOperationException("Refresh with unchanged generation must report no change.");
    }

    private static DysonAgentTurn Normal(string instruction) =>
        new()
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = instruction,
            AssistantText = "reply",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };

    private sealed class CountingTokenCounter : IDysonTokenCounter
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public void Reset() => Volatile.Write(ref _callCount, 0);

        public int CountTokens(string text)
        {
            Interlocked.Increment(ref _callCount);
            return text.Length;
        }
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession : DysonAgentSession
    {
        public StubSession(string mode, IDysonTokenCounter? tokenCounter = null)
            : base(mode, new DysonAgentSessionConfig(), new StubProvider())
        {
            if (tokenCounter is not null)
                TokenCounter = tokenCounter;
        }

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
