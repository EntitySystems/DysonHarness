using System.Collections.Concurrent;

namespace DysonHarness.Tests;

public sealed class DysonPipelineEventRelayTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Session = Guid.NewGuid();

    private static DysonPipelineEvent Evt(
        Guid wd, DysonPipelineEventKind kind, long run, long? job = null,
        DysonPipelineState state = DysonPipelineState.Running, string label = "Running") =>
        new(wd, kind, run, job, "CI", "main", "abcdef1234567", job is null ? null : $"job{job}", state, label,
            $"https://x/{run}", null, T0);

    private sealed class Rig : IDisposable
    {
        public DysonMessageBus Bus { get; } = new();
        public DysonVersionControlRegistry Registry { get; }
        public Guid Wd { get; } = Guid.NewGuid();
        public ConcurrentQueue<(Guid Session, string Text, DateTimeOffset At)> Delivered { get; } = new();
        public DysonPipelineEventRelay Relay { get; }

        public Rig(TimeSpan window, TimeSpan minGap, bool throwOnFirstDelivery = false)
        {
            var thrown = !throwOnFirstDelivery;
            var seams = new DysonVersionControlSeams(
                (span, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct),
                () => T0,
                (_, _) => Task.FromResult(Result<string, string>.AsValue("https://github.com/o/r.git")));
            Registry = new DysonVersionControlRegistry(Bus, [() => new QuietProvider()], seams);
            Relay = new DysonPipelineEventRelay(
                Bus,
                (id, text, _) =>
                {
                    if (!thrown)
                    {
                        thrown = true;
                        throw new InvalidOperationException("boom");
                    }

                    Delivered.Enqueue((id, text, DateTimeOffset.UtcNow));
                    return Task.FromResult(VoidResult<string>.Success);
                },
                (span, ct) => Task.Delay(span, ct),
                () => DateTimeOffset.UtcNow,
                window,
                minGap);
        }

        public DysonPipelineRelayTarget Target(Guid? session = null) => new(
            session ?? Session,
            Wd,
            _ => Task.FromResult(Registry.GetOrCreate(new DysonWorkDirectoryEntity
            {
                Id = Wd, SubjectId = "local", AbsolutePath = "E:/repo", GitOrigin = "https://github.com/o/r.git",
            })));

        public void Publish(DysonPipelineEvent evt) => Bus.Publish(DysonBusScopes.WorkDirectory(Wd), evt);

        public async Task<(Guid Session, string Text, DateTimeOffset At)> NextAsync(int index, int seconds = 5)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
            while (Delivered.Count <= index && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.True(Delivered.Count > index, "no delivery");
            return Delivered.ToArray()[index];
        }

        public void Dispose()
        {
            Relay.Dispose();
            Registry.Dispose();
            Bus.Dispose();
        }
    }

    [Fact]
    public async Task A_burst_is_one_batch_and_the_newest_state_wins()
    {
        using var rig = new Rig(TimeSpan.FromMilliseconds(150), TimeSpan.Zero);
        await rig.Relay.SyncAsync([rig.Target()]);

        rig.Publish(Evt(rig.Wd, DysonPipelineEventKind.RunStarted, 1));
        rig.Publish(Evt(rig.Wd, DysonPipelineEventKind.JobStarted, 1, 10));
        rig.Publish(Evt(rig.Wd, DysonPipelineEventKind.JobCompleted, 1, 10, DysonPipelineState.Succeeded, "Succeeded"));

        var first = await rig.NextAsync(0);
        await Task.Delay(400);
        Assert.Single(rig.Delivered);
        Assert.Equal(Session, first.Session);
        Assert.Contains(DysonPipelineEventRelay.Instruction, first.Text, StringComparison.Ordinal);
        Assert.Contains("job 10", first.Text, StringComparison.Ordinal);
        Assert.Contains("completed: Succeeded", first.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("job 10 \"job10\" (run 1 \"CI\"): started", first.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_throwing_delivery_does_not_stop_later_batches()
    {
        using var rig = new Rig(TimeSpan.FromMilliseconds(20), TimeSpan.Zero, throwOnFirstDelivery: true);
        await rig.Relay.SyncAsync([rig.Target()]);

        rig.Publish(Evt(rig.Wd, DysonPipelineEventKind.RunStarted, 1));
        await Task.Delay(300);
        rig.Publish(Evt(rig.Wd, DysonPipelineEventKind.RunStarted, 2));

        var delivered = await rig.NextAsync(0);
        Assert.Contains("run 2", delivered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_survives_the_same_run_restarting_in_one_batch()
    {
        var wd = Guid.NewGuid();
        var batch = new DysonPipelineEventBatch();
        batch.Add(Evt(wd, DysonPipelineEventKind.RunCompleted, 1, null, DysonPipelineState.Failed, "Failed"));
        batch.Add(Evt(wd, DysonPipelineEventKind.RunStarted, 1));

        Assert.Contains("completed: Failed", batch.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_flushes_the_window_early()
    {
        using var rig = new Rig(TimeSpan.FromSeconds(30), TimeSpan.Zero);
        await rig.Relay.SyncAsync([rig.Target()]);

        rig.Publish(Evt(rig.Wd, DysonPipelineEventKind.RunCompleted, 1, null, DysonPipelineState.Failed, "Failed"));

        var batch = await rig.NextAsync(0, seconds: 3);
        Assert.Contains("completed: Failed", batch.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deliveries_keep_the_minimum_gap_and_nothing_is_dropped()
    {
        using var rig = new Rig(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(400));
        await rig.Relay.SyncAsync([rig.Target()]);

        rig.Publish(Evt(rig.Wd, DysonPipelineEventKind.RunStarted, 1));
        await rig.NextAsync(0);
        rig.Publish(Evt(rig.Wd, DysonPipelineEventKind.RunStarted, 2));
        var second = await rig.NextAsync(1);

        Assert.True(second.At - rig.Delivered.ToArray()[0].At >= TimeSpan.FromMilliseconds(350));
        Assert.Contains("run 2", second.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_run_is_capped_and_the_overflow_is_counted()
    {
        using var rig = new Rig(TimeSpan.FromMilliseconds(150), TimeSpan.Zero);
        await rig.Relay.SyncAsync([rig.Target()]);

        for (var job = 1; job <= 12; job++)
            rig.Publish(Evt(rig.Wd, DysonPipelineEventKind.JobStarted, 1, job));

        var batch = await rig.NextAsync(0);
        Assert.Contains("+4 more updates for run 1", batch.Text, StringComparison.Ordinal);
        Assert.True(batch.Text.Length <= DysonPipelineEventRelay.Instruction.Length + DysonPipelineEventRelay.MaxLinesChars + 200);
    }

    [Fact]
    public void Instruction_keeps_the_chatter_rules()
    {
        var text = DysonPipelineEventRelay.Instruction;
        foreach (var phrase in new[]
        {
            "not a message from the user", "NO user-visible message", "silence", "no tool calls", "FAILED",
            "PipelineRun", "same surfaceId", "Never repeat", "unless the user asked you to",
        })
        {
            Assert.Contains(phrase, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Lease_follows_the_sessions_and_a_removed_session_gets_nothing()
    {
        using var rig = new Rig(TimeSpan.FromMilliseconds(50), TimeSpan.Zero);
        await rig.Relay.SyncAsync([rig.Target()]);
        Assert.Equal(1, rig.Relay.LeasedWorkDirectoryCount);

        await rig.Relay.SyncAsync([]);
        Assert.Equal(0, rig.Relay.LeasedWorkDirectoryCount);
        rig.Publish(Evt(rig.Wd, DysonPipelineEventKind.RunCompleted, 1, null, DysonPipelineState.Failed, "Failed"));
        await Task.Delay(300);
        Assert.Empty(rig.Delivered);
    }

    [Fact]
    public async Task Unresolvable_work_directory_never_leases_or_delivers()
    {
        using var rig = new Rig(TimeSpan.FromMilliseconds(50), TimeSpan.Zero);
        var cloud = new DysonPipelineRelayTarget(
            Session, rig.Wd,
            _ => Task.FromResult(Result<DysonVersionControlService, string>.AsError("Pipelines are not available in this host")));
        await rig.Relay.SyncAsync([cloud]);
        await rig.Relay.SyncAsync([cloud]);

        Assert.Equal(0, rig.Relay.LeasedWorkDirectoryCount);
    }

    [Fact]
    public async Task Dispose_drops_the_lease()
    {
        var rig = new Rig(TimeSpan.FromMilliseconds(50), TimeSpan.Zero);
        await rig.Relay.SyncAsync([rig.Target()]);
        Assert.Equal(1, rig.Relay.LeasedWorkDirectoryCount);
        rig.Relay.Dispose();
        Assert.Equal(0, rig.Relay.LeasedWorkDirectoryCount);
        rig.Dispose();
    }

    [Fact]
    public async Task Bus_publish_does_not_wait_for_delivery()
    {
        using var bus = new DysonMessageBus();
        var wd = Guid.NewGuid();
        var release = new TaskCompletionSource();
        using var relay = new DysonPipelineEventRelay(
            bus,
            async (_, _, _) =>
            {
                await release.Task;
                return VoidResult<string>.Success;
            },
            (span, ct) => Task.Delay(span, ct),
            () => DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.Zero);
        using var registry = new DysonVersionControlRegistry(bus, [() => new QuietProvider()], new DysonVersionControlSeams(
            (span, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct),
            () => T0,
            (_, _) => Task.FromResult(Result<string, string>.AsValue("https://github.com/o/r.git"))));
        await relay.SyncAsync([new DysonPipelineRelayTarget(Session, wd, _ => Task.FromResult(registry.GetOrCreate(
            new DysonWorkDirectoryEntity { Id = wd, SubjectId = "local", AbsolutePath = "E:/repo", GitOrigin = "https://github.com/o/r.git" })))]);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
            bus.Publish(DysonBusScopes.WorkDirectory(wd), Evt(wd, DysonPipelineEventKind.RunStarted, i));
        Assert.True(sw.ElapsedMilliseconds < 1000);
        release.SetResult();
    }

    private sealed class QuietProvider : IVersionControlProvider
    {
        public string Id => "github";
        public string DisplayName => "GitHub";
        public void ResetProbe() { }

        public Result<DysonPipelineTarget, DysonPipelineError> TryResolveTarget(string origin) =>
            Result<DysonPipelineTarget, DysonPipelineError>.AsValue(new DysonPipelineTarget("github.com", "o", "r", origin));

        public Task<Result<DysonPipelineFetch<DysonPipelineRun>, DysonPipelineError>> ListRunsAsync(
            DysonPipelineTarget target, string? etag, CancellationToken ct) =>
            Task.FromResult(Result<DysonPipelineFetch<DysonPipelineRun>, DysonPipelineError>.AsValue(
                new DysonPipelineFetch<DysonPipelineRun>(false, [], null, 4000, null)));

        public Task<Result<DysonPipelineFetch<DysonPipelineJob>, DysonPipelineError>> ListJobsAsync(
            DysonPipelineTarget target, long runId, string? etag, CancellationToken ct) =>
            Task.FromResult(Result<DysonPipelineFetch<DysonPipelineJob>, DysonPipelineError>.AsValue(
                new DysonPipelineFetch<DysonPipelineJob>(false, [], null, null, null)));

        public Task<Result<DysonPipelineRun, DysonPipelineError>> GetRunAsync(
            DysonPipelineTarget target, long runId, CancellationToken ct) =>
            Task.FromResult(Result<DysonPipelineRun, DysonPipelineError>.AsError(
                new DysonPipelineError(DysonPipelineErrorKind.RepoNotFound, "404")));

        public Task<Result<DysonPipelineJob, DysonPipelineError>> GetJobAsync(
            DysonPipelineTarget target, long jobId, CancellationToken ct) =>
            Task.FromResult(Result<DysonPipelineJob, DysonPipelineError>.AsError(
                new DysonPipelineError(DysonPipelineErrorKind.RepoNotFound, "404")));
    }
}
