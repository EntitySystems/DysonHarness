namespace DysonHarness;

/// <summary>One job row of a PipelineRun card. <see cref="Message"/> set means a placeholder line ("Job 7 not found").</summary>
public sealed record DysonA2uiPipelineJobLine(
    long Id, string Name, string StateLabel, string StateClass, bool IsRunning, bool IsActive,
    string? Duration, string? OpenFunc, string? Message);

/// <summary>A run header: "ci #12 · attempt 2", title, meta line, state, Open func.</summary>
public sealed record DysonA2uiPipelineRunLine(
    long RunId, string Header, string Title, string Meta, string StateLabel, string StateClass,
    bool IsRunning, bool IsActive, string? OpenFunc);

/// <summary>A run (null for the trailing "not found" group) with the job rows shown under it and the count cut off.</summary>
public sealed record DysonA2uiPipelineGroup(
    DysonA2uiPipelineRunLine? Run, IReadOnlyList<DysonA2uiPipelineJobLine> Jobs, int More);

/// <summary>Everything the PipelineRun component draws. <see cref="Message"/> replaces the groups when set.</summary>
public sealed record DysonA2uiPipelineRunCard(string? Message, IReadOnlyList<DysonA2uiPipelineGroup> Groups)
{
    /// <summary>The runId variant shows at most this many job rows, then "+N more".</summary>
    public const int MaxJobRows = 30;

    public bool IsActive => Groups.Any(g => g.Run is { IsActive: true } || g.Jobs.Any(j => j.IsActive));

    /// <summary>runId variant: the run and all of its jobs. Never throws; failures become <see cref="Message"/>.</summary>
    public static DysonA2uiPipelineRunCard Build(
        long runId, DysonPipelineRun? run, IReadOnlyList<DysonPipelineJob>? jobs, DysonPipelineError? error,
        bool detected, DateTimeOffset now)
    {
        if (!detected)
            return Fail(NoRepo);
        if (run is null)
            return Fail(error is null ? $"Run {runId} not found" : ErrorText(error, $"Run {runId}"));

        var list = jobs ?? [];
        var rows = list.Take(MaxJobRows).Select(j => JobLine(j, now)).ToList();
        return new DysonA2uiPipelineRunCard(null, [new(RunLine(run, now), rows, Math.Max(0, list.Count - MaxJobRows))]);
    }

    /// <summary>
    /// jobIds variant: found jobs grouped by their run in the order the ids were given, each run named once,
    /// then one trailing group with a "Job {id} not found" line per id that was not found.
    /// </summary>
    public static DysonA2uiPipelineRunCard BuildJobs(
        IReadOnlyList<long> jobIds, IReadOnlyDictionary<long, DysonPipelineJob> found,
        IReadOnlyDictionary<long, DysonPipelineRun> runs, DysonPipelineError? error, bool detected, DateTimeOffset now)
    {
        if (!detected)
            return Fail(NoRepo);

        var order = new List<long>();
        var byRun = new Dictionary<long, List<DysonA2uiPipelineJobLine>>();
        var missing = new List<DysonA2uiPipelineJobLine>();
        foreach (var id in jobIds)
        {
            if (found.TryGetValue(id, out var job))
            {
                if (!byRun.TryGetValue(job.RunId, out var lines))
                {
                    lines = [];
                    byRun[job.RunId] = lines;
                    order.Add(job.RunId);
                }

                lines.Add(JobLine(job, now));
            }
            else
                missing.Add(new DysonA2uiPipelineJobLine(id, "", "", "neutral", false, false, null, null, $"Job {id} not found"));
        }

        if (byRun.Count == 0 && error is not null)
            return Fail(ErrorText(error, "Job"));

        var groups = new List<DysonA2uiPipelineGroup>(order.Count + 1);
        foreach (var runId in order)
        {
            var line = runs.TryGetValue(runId, out var run)
                ? RunLine(run, now)
                : new DysonA2uiPipelineRunLine(runId, $"Run {runId}", "", "", "", "neutral", false, false, null);
            groups.Add(new DysonA2uiPipelineGroup(line, byRun[runId], 0));
        }

        if (missing.Count > 0)
            groups.Add(new DysonA2uiPipelineGroup(null, missing, 0));
        return new DysonA2uiPipelineRunCard(null, groups);
    }

    internal const string NoRepo = "No GitHub repository for this work directory";

    /// <summary>Card text for a pipeline error. <paramref name="subject"/> is "Run 12" or "Job".</summary>
    internal static string ErrorText(DysonPipelineError error, string subject) => error.Kind switch
    {
        DysonPipelineErrorKind.NotDetected => NoRepo,
        DysonPipelineErrorKind.CliMissing => "GitHub CLI (gh) is not installed",
        DysonPipelineErrorKind.NotAuthenticated => "gh is not signed in",
        DysonPipelineErrorKind.RepoNotFound => subject == "Job" ? "Job not found" : $"{subject} not found",
        DysonPipelineErrorKind.RateLimited => "GitHub rate limit, retrying",
        _ => subject == "Job" ? "Could not load jobs" : $"Could not load {subject.ToLowerInvariant()}",
    };

    private static DysonA2uiPipelineRunCard Fail(string message) => new(message, []);

    private static bool Active(DysonPipelineState state) =>
        state is DysonPipelineState.Queued or DysonPipelineState.Waiting or DysonPipelineState.Running;

    private static DysonA2uiPipelineRunLine RunLine(DysonPipelineRun run, DateTimeOffset now)
    {
        var header = $"{run.WorkflowName} #{run.RunNumber}";
        if (run.Attempt > 1)
            header += $" \u00b7 attempt {run.Attempt}";

        var parts = new List<string>(4);
        if (run.Branch.Length > 0)
            parts.Add(run.Branch);
        if (run.HeadSha.Length > 0)
            parts.Add(DysonPipelineText.ShortSha(run.HeadSha));
        if (run.Event.Length > 0)
            parts.Add(run.Event);
        var active = Active(run.State);
        if (active)
        {
            if (DysonPipelineText.Elapsed(run, now) is { } elapsed)
                parts.Add(DysonPipelineText.FormatDuration(elapsed));
        }
        else
            parts.Add(DysonPipelineText.FormatRelative(run.UpdatedAt, now));

        return new DysonA2uiPipelineRunLine(
            run.Id, header, run.Title, string.Join(" \u00b7 ", parts), run.StateLabel,
            DysonPipelineToolJson.StateName(run.State), run.State == DysonPipelineState.Running, active,
            OpenFunc(run.HtmlUrl));
    }

    private static DysonA2uiPipelineJobLine JobLine(DysonPipelineJob job, DateTimeOffset now) =>
        new(job.Id, job.Name, job.StateLabel, DysonPipelineToolJson.StateName(job.State),
            job.State == DysonPipelineState.Running, Active(job.State),
            DysonPipelineText.Elapsed(job, now) is { } elapsed ? DysonPipelineText.FormatDuration(elapsed) : null,
            OpenFunc(job.HtmlUrl), null);

    /// <summary>Host data, not model data; still http(s) only so the func always resolves.</summary>
    private static string? OpenFunc(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "open_url:" + url
            : null;
}
