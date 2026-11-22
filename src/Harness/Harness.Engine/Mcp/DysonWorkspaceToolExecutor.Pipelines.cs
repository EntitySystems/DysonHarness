using System.Collections.Concurrent;
using System.Text.Json;

namespace DysonHarness;

public sealed partial class DysonWorkspaceToolExecutor
{
    private const string PipelinesUnavailable = "Pipelines are not available in this host";

    /// <summary>
    /// ListPipelineRuns: the latest 20 GitHub Actions runs of this work directory, optionally with their jobs.
    /// Reads the host's pipeline service (snapshot first, at most one gh call per run list); every failure is an
    /// IsError result that names the cause. Writes nothing.
    /// </summary>
    private async Task<DysonToolCallResult> ListPipelineRunsAsync(DysonToolCall call, CancellationToken cancellationToken)
    {
        var denied = RejectUnlessMetaAgent(call, "ListPipelineRuns");
        if (denied is not null)
            return denied;

        var includeJobs = false;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Error(call, "ListPipelineRuns: invalid JSON arguments.");
            if (root.TryGetProperty("includeJobs", out var flag) && flag.ValueKind != JsonValueKind.Null)
            {
                if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Error(call, "ListPipelineRuns: includeJobs must be a boolean.");
                includeJobs = flag.GetBoolean();
            }
        }
        catch (JsonException)
        {
            return Error(call, "ListPipelineRuns: invalid JSON arguments.");
        }

        var hook = _session.Config.Pipelines;
        if (hook is null || _workDirectoryId == Guid.Empty)
            return Error(call, PipelinesUnavailable);

        var resolved = await hook(_workDirectoryId, cancellationToken).ConfigureAwait(false);
        if (resolved.IsError)
            return Error(call, resolved.Error);

        var service = resolved.Value;
        var runs = await service.GetRecentRunsAsync(cancellationToken).ConfigureAwait(false);
        if (runs.IsError)
            return Error(call, DysonPipelineToolJson.ErrorMessage(runs.Error, "runs"));

        var target = service.Target;
        if (target is null)
            return Error(call, DysonPipelineToolJson.ErrorMessage(
                new DysonPipelineError(DysonPipelineErrorKind.NotDetected, ""), "runs"));

        var jobs = includeJobs
            ? await CollectJobsAsync(service, runs.Value, cancellationToken).ConfigureAwait(false)
            : null;
        return Ok(call, DysonPipelineToolJson.Build(target, runs.Value, jobs, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Snapshot jobs cost no call. The other runs, newest first, cost one ListJobs call each up to
    /// <see cref="DysonVersionControlService.MaxToolJobFetches"/> (4 in flight); the rest carry a note.
    /// One failing run gets a note and does not fail the call.
    /// </summary>
    private static async Task<IReadOnlyDictionary<long, DysonPipelineToolJobs>> CollectJobsAsync(
        DysonVersionControlService service, IReadOnlyList<DysonPipelineRun> runs, CancellationToken cancellationToken)
    {
        var known = service.Snapshot?.Jobs;
        var result = new ConcurrentDictionary<long, DysonPipelineToolJobs>();
        var toFetch = new List<DysonPipelineRun>();
        foreach (var run in runs)
        {
            if (known is not null && known.TryGetValue(run.Id, out var cached))
                result[run.Id] = new DysonPipelineToolJobs(cached, null);
            else if (toFetch.Count < DysonVersionControlService.MaxToolJobFetches)
                toFetch.Add(run);
            else
                result[run.Id] = new DysonPipelineToolJobs(null, DysonPipelineToolJson.NotFetchedNote);
        }

        await Parallel.ForEachAsync(
            toFetch,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (run, token) =>
            {
                var fetched = await service.GetJobsAsync(run.Id, token).ConfigureAwait(false);
                result[run.Id] = fetched.IsError
                    ? new DysonPipelineToolJobs(null, DysonPipelineToolJson.ErrorMessage(fetched.Error, "jobs"))
                    : new DysonPipelineToolJobs(fetched.Value, null);
            }).ConfigureAwait(false);

        return result;
    }
}
