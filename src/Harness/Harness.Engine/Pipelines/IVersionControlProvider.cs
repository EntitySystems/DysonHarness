namespace DysonHarness;

public interface IVersionControlProvider
{
    string Id { get; }            // "github" — stable; also the UI icon key (icons/providers/{Id}.svg)
    string DisplayName { get; }   // "GitHub"
    /// <summary>Pure, no I/O: does this provider own the remote? Returns the target or NotDetected.</summary>
    Result<DysonPipelineTarget, DysonPipelineError> TryResolveTarget(string origin);
    Task<Result<DysonPipelineFetch<DysonPipelineRun>, DysonPipelineError>> ListRunsAsync(
        DysonPipelineTarget target, string? etag, CancellationToken ct);
    Task<Result<DysonPipelineFetch<DysonPipelineJob>, DysonPipelineError>> ListJobsAsync(
        DysonPipelineTarget target, long runId, string? etag, CancellationToken ct);
    /// <summary>One run by id, outside the recent-20 list. No ETag. A missing run is a RepoNotFound error.</summary>
    Task<Result<DysonPipelineRun, DysonPipelineError>> GetRunAsync(
        DysonPipelineTarget target, long runId, CancellationToken ct);
    /// <summary>One job by id; <see cref="DysonPipelineJob.RunId"/> names its run. No ETag.</summary>
    Task<Result<DysonPipelineJob, DysonPipelineError>> GetJobAsync(
        DysonPipelineTarget target, long jobId, CancellationToken ct);
}

/// <summary>Lets each work-directory instance create its own provider instances.</summary>
public delegate IVersionControlProvider DysonVersionControlProviderFactory();
