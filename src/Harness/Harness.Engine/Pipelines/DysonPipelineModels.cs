namespace DysonHarness;

public enum DysonPipelineState { Queued, Waiting, Running, Succeeded, Failed, Cancelled, Skipped, Neutral }

public enum DysonPipelineErrorKind { NotDetected, CliMissing, NotAuthenticated, RepoNotFound, RateLimited, Failed }

public sealed record DysonPipelineError(DysonPipelineErrorKind Kind, string Message, DateTimeOffset? RetryAfter = null);

/// <summary>Repo the provider resolved from the remote: host (github.com or GHES), owner, name, web URL.</summary>
public sealed record DysonPipelineTarget(string Host, string Owner, string Name, string WebUrl);

public sealed record DysonPipelineRun(
    long Id, string WorkflowName, string Title, string Branch, string HeadSha, string Event,
    int RunNumber, int Attempt, DysonPipelineState State, string StateLabel,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset UpdatedAt, string HtmlUrl);

public sealed record DysonPipelineJob(
    long Id, long RunId, string Name, DysonPipelineState State, string StateLabel,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string HtmlUrl);

/// <summary>One conditional fetch. NotModified =&gt; Items is empty and the caller keeps its previous list.</summary>
public sealed record DysonPipelineFetch<T>(bool NotModified, IReadOnlyList<T> Items, string? ETag, int? RateRemaining, DateTimeOffset? RateReset);

/// <summary>What the UI renders. Immutable; replaced wholesale by the work directory's DysonVersionControlService.</summary>
public sealed record DysonPipelineSnapshot(
    Guid WorkDirectoryId, string? ProviderId, string? ProviderName, DysonPipelineTarget? Target,
    IReadOnlyList<DysonPipelineRun> Runs, IReadOnlyDictionary<long, IReadOnlyList<DysonPipelineJob>> Jobs,
    int RunningCount, int QueuedCount, DysonPipelineError? Error, DateTimeOffset? FetchedAt)
{
    public int ActiveCount => RunningCount + QueuedCount;   // the pill
    public bool IsDetected => ProviderId is not null;
}

/// <summary>UI-agnostic text for pipeline durations, relative times and short shas.</summary>
public static class DysonPipelineText
{
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;
        var total = (long)duration.TotalSeconds;
        if (total < 60)
            return $"{total}s";
        if (total < 3600)
            return $"{total / 60}m {total % 60:00}s";
        var hours = total / 3600;
        var minutes = (total % 3600) / 60;
        return $"{hours}h {minutes:00}m";
    }

    public static string FormatRelative(DateTimeOffset when, DateTimeOffset now)
    {
        var elapsed = now - when;
        if (elapsed < TimeSpan.FromMinutes(1))
            return "just now";
        if (elapsed < TimeSpan.FromHours(1))
            return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed < TimeSpan.FromDays(1))
            return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }

    public static string ShortSha(string sha) =>
        sha.Length <= 7 ? sha : sha[..7];

    /// <summary>Running: now-StartedAt. Completed: UpdatedAt-StartedAt, or CreatedAt when the run never started.</summary>
    public static TimeSpan? Elapsed(DysonPipelineRun run, DateTimeOffset now)
    {
        if (run.State is DysonPipelineState.Queued or DysonPipelineState.Waiting or DysonPipelineState.Running)
            return run.StartedAt is { } started ? now - started : null;
        var from = run.StartedAt ?? run.CreatedAt;
        return run.UpdatedAt - from;
    }

    /// <summary>Completed: CompletedAt-StartedAt. Still going: now-StartedAt.</summary>
    public static TimeSpan? Elapsed(DysonPipelineJob job, DateTimeOffset now)
    {
        if (job.StartedAt is not { } started)
            return null;
        return (job.CompletedAt ?? now) - started;
    }
}
