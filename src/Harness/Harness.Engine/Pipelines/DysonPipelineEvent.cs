namespace DysonHarness;

public enum DysonPipelineEventKind { RunCreated, RunStarted, RunCompleted, JobStarted, JobCompleted }

/// <summary>
/// One pipeline transition, published by <see cref="DysonVersionControlService"/> on the work directory's bus scope.
/// <see cref="DysonPipelinesChangedEvent"/> (no body, for the UI) is unchanged. Provider errors never produce one.
/// </summary>
public sealed record DysonPipelineEvent(
    Guid WorkDirectoryId,
    DysonPipelineEventKind Kind,
    long RunId,
    long? JobId,
    string Workflow,
    string Branch,
    string HeadSha,
    string? JobName,
    DysonPipelineState State,
    string StateLabel,
    string HtmlUrl,
    TimeSpan? Elapsed,
    DateTimeOffset At) : IDysonMessageBusEvent
{
    /// <summary>A completion that failed, timed out (folded into Failed) or was cancelled.</summary>
    public bool IsImportant =>
        Kind is DysonPipelineEventKind.RunCompleted or DysonPipelineEventKind.JobCompleted
        && State is DysonPipelineState.Failed or DysonPipelineState.Cancelled;
}

/// <summary>Pure diff of two snapshots into events. No I/O, no clock reads.</summary>
public static class DysonPipelineEventDiff
{
    /// <summary>
    /// <paramref name="previous"/> null is the silent baseline (no events). A run id seen for the first time is
    /// RunCreated; a state change to Running is RunStarted; to a terminal state RunCompleted. Jobs follow the same
    /// rule, except that a run already known whose jobs were never fetched yet is a silent job baseline.
    /// A new Attempt of a known run counts as a fresh state (Started or Completed, no Created). Skipped emits nothing.
    /// </summary>
    public static IReadOnlyList<DysonPipelineEvent> Diff(
        Guid workDirectoryId, DysonPipelineSnapshot? previous, DysonPipelineSnapshot next, DateTimeOffset now)
    {
        // A provider error keeps the old runs, and an undetected snapshot has none: neither may replay history.
        // A first-tick failure leaves an empty run list as "previous"; the tick after it is the baseline.
        if (previous is not { IsDetected: true } || next.Error is not null
            || (previous.Error is not null && previous.Runs.Count == 0))
            return [];

        var events = new List<DysonPipelineEvent>();
        var prevRuns = new Dictionary<long, DysonPipelineRun>();
        foreach (var run in previous.Runs)
            prevRuns[run.Id] = run;

        foreach (var run in next.Runs)
        {
            var known = prevRuns.TryGetValue(run.Id, out var before);
            if (!known)
            {
                events.Add(RunEvent(workDirectoryId, DysonPipelineEventKind.RunCreated, run, now));
                AddRunTransition(events, workDirectoryId, run, now);
            }
            else if (before!.Attempt != run.Attempt || before.State != run.State)
                AddRunTransition(events, workDirectoryId, run, now);
        }

        var runsById = next.Runs.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var (runId, jobs) in next.Jobs)
        {
            if (!runsById.TryGetValue(runId, out var run))
                continue;
            Dictionary<long, DysonPipelineJob>? prevJobs = null;
            if (previous.Jobs.TryGetValue(runId, out var before))
                prevJobs = before.ToDictionary(j => j.Id);
            else if (prevRuns.ContainsKey(runId))
                continue;   // known run, jobs fetched for the first time: baseline

            foreach (var job in jobs)
            {
                if (prevJobs is not null && prevJobs.TryGetValue(job.Id, out var old) && old.State == job.State)
                    continue;
                if (IsTerminal(job.State))
                    events.Add(JobEvent(workDirectoryId, DysonPipelineEventKind.JobCompleted, run, job, now));
                else if (job.State == DysonPipelineState.Running)
                    events.Add(JobEvent(workDirectoryId, DysonPipelineEventKind.JobStarted, run, job, now));
            }
        }

        return events;
    }

    private static bool IsTerminal(DysonPipelineState state) =>
        state is DysonPipelineState.Succeeded or DysonPipelineState.Failed
            or DysonPipelineState.Cancelled or DysonPipelineState.Neutral;

    private static void AddRunTransition(List<DysonPipelineEvent> events, Guid wd, DysonPipelineRun run, DateTimeOffset now)
    {
        if (run.State == DysonPipelineState.Running)
            events.Add(RunEvent(wd, DysonPipelineEventKind.RunStarted, run, now));
        else if (IsTerminal(run.State))
            events.Add(RunEvent(wd, DysonPipelineEventKind.RunCompleted, run, now));
    }

    private static DysonPipelineEvent RunEvent(Guid wd, DysonPipelineEventKind kind, DysonPipelineRun run, DateTimeOffset now) =>
        new(wd, kind, run.Id, null, run.WorkflowName, run.Branch, run.HeadSha, null,
            run.State, run.StateLabel, run.HtmlUrl, DysonPipelineText.Elapsed(run, now), now);

    private static DysonPipelineEvent JobEvent(
        Guid wd, DysonPipelineEventKind kind, DysonPipelineRun run, DysonPipelineJob job, DateTimeOffset now) =>
        new(wd, kind, run.Id, job.Id, run.WorkflowName, run.Branch, run.HeadSha, job.Name,
            job.State, job.StateLabel, string.IsNullOrEmpty(job.HtmlUrl) ? run.HtmlUrl : job.HtmlUrl,
            DysonPipelineText.Elapsed(job, now), now);
}
