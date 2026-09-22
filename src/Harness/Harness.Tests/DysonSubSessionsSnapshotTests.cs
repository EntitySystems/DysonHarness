using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// <see cref="DysonAgentSession.SubSessions"/> is a snapshot. Unregister must not throw
/// or yield null while another thread enumerates it (the Meta Agent circuit crash).
/// </summary>
public class DysonSubSessionsSnapshotTests
{
    [Fact]
    public async Task Concurrent_unregister_does_not_throw_or_yield_null()
    {
        const int childCount = 128;
        var parent = new StubSession();
        var children = new StubSession[childCount];
        for (var i = 0; i < childCount; i++)
        {
            children[i] = new StubSession();
            parent.RegisterForTest(children[i]);
        }

        using var start = new Barrier(5);
        var readersInLoop = 0;
        var writerDone = 0;
        var writer = Task.Run(() =>
        {
            start.SignalAndWait();
            while (Volatile.Read(ref readersInLoop) < 4)
                Thread.SpinWait(1);

            foreach (var child in children)
                Assert.True(parent.UnregisterSubagent(child.Id));
            Volatile.Write(ref writerDone, 1);
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            Interlocked.Increment(ref readersInLoop);
            while (Volatile.Read(ref writerDone) == 0)
                Enumerate(parent);

            Enumerate(parent);
        })).ToArray();

        await Task.WhenAll(readers.Append(writer));
        Assert.Empty(parent.SubSessions);
    }

    private static void Enumerate(DysonAgentSession parent)
    {
        foreach (var child in parent.SubSessions)
        {
            Assert.NotNull(child);
            _ = child.PersistenceId;
            _ = child.Id;
        }

        // Same walk as MetaAgent.ChildIds: List.Where can observe a nulled slot.
        var ids = parent.SubSessions
            .Where(child => child.PersistenceId != Guid.Empty)
            .Select(child => child.Id)
            .ToArray();
        Assert.NotNull(ids);
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.MetaAgent,
        new DysonAgentSessionConfig(),
        new StubProvider())
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
