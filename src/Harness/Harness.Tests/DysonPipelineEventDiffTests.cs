namespace DysonHarness.Tests;

public sealed class DysonPipelineEventDiffTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Wd = Guid.NewGuid();

    private static DysonPipelineRun Run(long id, DysonPipelineState state, int attempt = 1, string label = "") => new(
        id, "CI", "t", "main", "abcdef1234567", "push", (int)id, attempt, state,
        label.Length > 0 ? label : state.ToString(), T0, T0, T0, $"https://x/runs/{id}");

    private static DysonPipelineJob Job(long id, long runId, DysonPipelineState state) => new(
        id, runId, "build", state, state.ToString(), T0, null, $"https://x/jobs/{id}");

    private static DysonPipelineSnapshot Snap(
        DysonPipelineRun[] runs, DysonPipelineJob[]? jobs = null, DysonPipelineError? error = null, bool detected = true)
    {
        var map = new Dictionary<long, IReadOnlyList<DysonPipelineJob>>();
        foreach (var g in (jobs ?? []).GroupBy(j => j.RunId))
            map[g.Key] = g.ToArray();
        return new DysonPipelineSnapshot(Wd, detected ? "github" : null, "GitHub", null, runs, map,
            runs.Count(r => r.State == DysonPipelineState.Running), 0, error, T0);
    }

    private static IReadOnlyList<DysonPipelineEvent> Diff(DysonPipelineSnapshot? a, DysonPipelineSnapshot b) =>
        DysonPipelineEventDiff.Diff(Wd, a, b, T0.AddSeconds(30));

    [Fact]
    public void First_tick_is_a_silent_baseline()
    {
        Assert.Empty(Diff(null, Snap([Run(1, DysonPipelineState.Running)])));
    }

    [Fact]
    public void No_change_yields_no_events()
    {
        var s = Snap([Run(1, DysonPipelineState.Running)], [Job(10, 1, DysonPipelineState.Running)]);
        Assert.Empty(Diff(s, Snap([Run(1, DysonPipelineState.Running)], [Job(10, 1, DysonPipelineState.Running)])));
    }

    [Fact]
    public void New_running_run_is_created_and_started_and_a_new_terminal_run_is_created_and_completed()
    {
        var events = Diff(Snap([]), Snap([Run(1, DysonPipelineState.Running), Run(2, DysonPipelineState.Succeeded)]));
        Assert.Equal(
            [DysonPipelineEventKind.RunCreated, DysonPipelineEventKind.RunStarted,
             DysonPipelineEventKind.RunCreated, DysonPipelineEventKind.RunCompleted],
            events.Select(e => e.Kind));
    }

    [Fact]
    public void Queued_to_running_is_started_and_running_to_failed_is_important()
    {
        var started = Diff(Snap([Run(1, DysonPipelineState.Queued)]), Snap([Run(1, DysonPipelineState.Running)]));
        Assert.Equal(DysonPipelineEventKind.RunStarted, Assert.Single(started).Kind);

        var done = Diff(Snap([Run(1, DysonPipelineState.Running)]), Snap([Run(1, DysonPipelineState.Failed, label: "Timed out")]));
        var evt = Assert.Single(done);
        Assert.Equal(DysonPipelineEventKind.RunCompleted, evt.Kind);
        Assert.True(evt.IsImportant);
        Assert.Equal("Timed out", evt.StateLabel);
    }

    [Fact]
    public void Succeeded_completion_is_not_important()
    {
        var evt = Assert.Single(Diff(Snap([Run(1, DysonPipelineState.Running)]), Snap([Run(1, DysonPipelineState.Succeeded)])));
        Assert.False(evt.IsImportant);
    }

    [Fact]
    public void Run_first_seen_already_completed_in_the_baseline_stays_silent()
    {
        var s = Snap([Run(1, DysonPipelineState.Succeeded)]);
        Assert.Empty(Diff(s, Snap([Run(1, DysonPipelineState.Succeeded)])));
    }

    [Fact]
    public void New_attempt_of_a_known_run_is_a_fresh_state()
    {
        var events = Diff(Snap([Run(1, DysonPipelineState.Failed)]), Snap([Run(1, DysonPipelineState.Running, attempt: 2)]));
        Assert.Equal(DysonPipelineEventKind.RunStarted, Assert.Single(events).Kind);
    }

    [Fact]
    public void Job_transitions_and_first_job_fetch_of_a_known_run_is_a_baseline()
    {
        var run = Run(1, DysonPipelineState.Running);
        // Jobs fetched for the first time for a known run: baseline, silent.
        Assert.Empty(Diff(Snap([run]), Snap([run], [Job(10, 1, DysonPipelineState.Running)])));

        var started = Diff(
            Snap([run], [Job(10, 1, DysonPipelineState.Queued)]),
            Snap([run], [Job(10, 1, DysonPipelineState.Running), Job(11, 1, DysonPipelineState.Queued)]));
        var s = Assert.Single(started);
        Assert.Equal((DysonPipelineEventKind.JobStarted, 10L, "build"), (s.Kind, s.JobId!.Value, s.JobName));

        var failed = Diff(
            Snap([run], [Job(10, 1, DysonPipelineState.Running)]),
            Snap([run], [Job(10, 1, DysonPipelineState.Failed)]));
        var f = Assert.Single(failed);
        Assert.Equal(DysonPipelineEventKind.JobCompleted, f.Kind);
        Assert.True(f.IsImportant);
    }

    [Theory]
    [InlineData(DysonPipelineErrorKind.CliMissing)]
    [InlineData(DysonPipelineErrorKind.NotAuthenticated)]
    [InlineData(DysonPipelineErrorKind.RateLimited)]
    public void Provider_errors_yield_no_events_and_the_recovery_tick_does_not_replay(DysonPipelineErrorKind kind)
    {
        var good = Snap([Run(1, DysonPipelineState.Running)]);
        var error = new DysonPipelineError(kind, "x");
        var failing = Snap([Run(1, DysonPipelineState.Running)], error: error);
        Assert.Empty(Diff(good, failing));

        // Not-detected / error-on-first-tick snapshot has no runs; the next good tick is a baseline.
        Assert.Empty(Diff(Snap([], error: error), good));
        Assert.Empty(Diff(Snap([], detected: false), good));
    }
}
