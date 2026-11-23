using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DysonHarness;

/// <summary>A run's jobs for <see cref="DysonPipelineToolJson"/>: the list, or why there is none.</summary>
public sealed record DysonPipelineToolJobs(IReadOnlyList<DysonPipelineJob>? Jobs, string? Note);

/// <summary>Pure JSON for the ListPipelineRuns tool result. No I/O.</summary>
public static class DysonPipelineToolJson
{
    /// <summary>The note carried by runs past <see cref="DysonVersionControlService.MaxToolJobFetches"/>.</summary>
    public const string NotFetchedNote = "not fetched; render PipelineRun runId to see them";

    /// <summary>
    /// <c>{"repo","fetchedAt","runs":[{id,workflow,title,branch,shortSha,event,runNumber,attempt,state,stateLabel,htmlUrl,elapsed,jobs?}]}</c>.
    /// <paramref name="jobsByRun"/> null means jobs were not requested (no <c>jobs</c> key); otherwise every run
    /// gets <c>jobs</c> (null plus <c>jobsNote</c> when it has no entry or the entry has no list).
    /// </summary>
    public static string Build(
        DysonPipelineTarget target, IReadOnlyList<DysonPipelineRun> runs,
        IReadOnlyDictionary<long, DysonPipelineToolJobs>? jobsByRun, DateTimeOffset now)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("repo", $"{target.Owner}/{target.Name}");
            w.WriteString("fetchedAt", now.ToString("o", CultureInfo.InvariantCulture));
            w.WriteStartArray("runs");
            foreach (var run in runs)
                WriteRun(w, run, jobsByRun, now);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>The tool error text for a pipeline error. <paramref name="what"/> is "runs" or "jobs".</summary>
    public static string ErrorMessage(DysonPipelineError error, string what) => error.Kind switch
    {
        DysonPipelineErrorKind.NotDetected => "No GitHub repository detected for this work directory",
        DysonPipelineErrorKind.CliMissing => "GitHub CLI (gh) is not installed",
        DysonPipelineErrorKind.NotAuthenticated => "gh is not signed in (run gh auth login)",
        DysonPipelineErrorKind.RepoNotFound => "Repository not found or no access",
        DysonPipelineErrorKind.RateLimited => error.RetryAfter is { } at
            ? $"GitHub rate limit; retry after {at.ToString("o", CultureInfo.InvariantCulture)}"
            : "GitHub rate limit; retry later",
        _ => $"Could not list {what}: {error.Message}",
    };

    /// <summary>camelCase of the enum name: Succeeded to succeeded, Queued to queued.</summary>
    public static string StateName(DysonPipelineState state)
    {
        var name = state.ToString();
        return string.Concat(char.ToLowerInvariant(name[0]).ToString(), name.AsSpan(1));
    }

    private static void WriteRun(
        Utf8JsonWriter w, DysonPipelineRun run, IReadOnlyDictionary<long, DysonPipelineToolJobs>? jobsByRun, DateTimeOffset now)
    {
        w.WriteStartObject();
        w.WriteNumber("id", run.Id);
        w.WriteString("workflow", run.WorkflowName);
        w.WriteString("title", run.Title);
        w.WriteString("branch", run.Branch);
        w.WriteString("shortSha", DysonPipelineText.ShortSha(run.HeadSha));
        w.WriteString("event", run.Event);
        w.WriteNumber("runNumber", run.RunNumber);
        w.WriteNumber("attempt", run.Attempt);
        w.WriteString("state", StateName(run.State));
        w.WriteString("stateLabel", run.StateLabel);
        w.WriteString("htmlUrl", run.HtmlUrl);
        WriteElapsed(w, DysonPipelineText.Elapsed(run, now));
        if (jobsByRun is not null)
        {
            jobsByRun.TryGetValue(run.Id, out var entry);
            if (entry?.Jobs is { } jobs)
            {
                w.WriteStartArray("jobs");
                foreach (var job in jobs)
                    WriteJob(w, job, now);
                w.WriteEndArray();
            }
            else
            {
                w.WriteNull("jobs");
                w.WriteString("jobsNote", entry?.Note ?? NotFetchedNote);
            }
        }

        w.WriteEndObject();
    }

    private static void WriteJob(Utf8JsonWriter w, DysonPipelineJob job, DateTimeOffset now)
    {
        w.WriteStartObject();
        w.WriteNumber("id", job.Id);
        w.WriteString("name", job.Name);
        w.WriteString("state", StateName(job.State));
        w.WriteString("stateLabel", job.StateLabel);
        WriteElapsed(w, DysonPipelineText.Elapsed(job, now));
        w.WriteEndObject();
    }

    private static void WriteElapsed(Utf8JsonWriter w, TimeSpan? elapsed)
    {
        if (elapsed is { } span)
            w.WriteString("elapsed", DysonPipelineText.FormatDuration(span));
        else
            w.WriteNull("elapsed");
    }
}
