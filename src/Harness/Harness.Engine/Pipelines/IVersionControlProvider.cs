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
}

/// <summary>Lets each work-directory instance create its own provider instances.</summary>
public delegate IVersionControlProvider DysonVersionControlProviderFactory();
