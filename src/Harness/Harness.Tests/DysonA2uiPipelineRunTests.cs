using System.Text.Json;

namespace DysonHarness.Tests;

/// <summary>PipelineRun card model (pure, no gh) and the single-object GitHub parsers it relies on.</summary>
public sealed class DysonA2uiPipelineRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static DysonPipelineRun Run(
        long id, DysonPipelineState state = DysonPipelineState.Succeeded, int attempt = 1, string url = "https://github.com/o/r/actions/runs/1") =>
        new(id, "ci", "title", "main", "abcdef1234567", "push", 12, attempt, state, state.ToString(),
            Now.AddMinutes(-10), Now.AddMinutes(-9), Now.AddMinutes(-5), url);

    private static DysonPipelineJob Job(long id, long runId, DysonPipelineState state = DysonPipelineState.Succeeded) =>
        new(id, runId, $"job{id}", state, state.ToString(), Now.AddMinutes(-3), state == DysonPipelineState.Running ? null : Now.AddMinutes(-2),
            $"https://github.com/o/r/actions/runs/{runId}/job/{id}");

    [Fact]
    public void Build_header_meta_and_open_func()
    {
        var card = DysonA2uiPipelineRunCard.Build(1, Run(1, attempt: 2), [Job(10, 1)], null, true, Now);

        Assert.Null(card.Message);
        var group = Assert.Single(card.Groups);
        Assert.Equal("ci #12 \u00b7 attempt 2", group.Run!.Header);
        Assert.Contains("abcdef1", group.Run.Meta, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef12", group.Run.Meta, StringComparison.Ordinal);
        Assert.Contains("5m ago", group.Run.Meta, StringComparison.Ordinal);
        Assert.Equal("open_url:https://github.com/o/r/actions/runs/1", group.Run.OpenFunc);
        Assert.Equal("succeeded", group.Run.StateClass);
        Assert.False(card.IsActive);
        Assert.Single(group.Jobs);
    }

    [Fact]
    public void Build_running_run_shows_a_duration_and_is_active()
    {
        var card = DysonA2uiPipelineRunCard.Build(1, Run(1, DysonPipelineState.Running), [Job(10, 1, DysonPipelineState.Running)], null, true, Now);

        var group = Assert.Single(card.Groups);
        Assert.True(group.Run!.IsRunning);
        Assert.Contains("9m 00s", group.Run.Meta, StringComparison.Ordinal);
        Assert.Equal("3m 00s", group.Jobs[0].Duration);
        Assert.True(card.IsActive);
    }

    [Fact]
    public void Build_caps_job_rows_and_counts_the_rest()
    {
        var jobs = Enumerable.Range(1, DysonA2uiPipelineRunCard.MaxJobRows + 5).Select(i => Job(i, 1)).ToList();

        var group = Assert.Single(DysonA2uiPipelineRunCard.Build(1, Run(1), jobs, null, true, Now).Groups);

        Assert.Equal(DysonA2uiPipelineRunCard.MaxJobRows, group.Jobs.Count);
        Assert.Equal(5, group.More);
    }

    [Fact]
    public void Build_without_http_url_has_no_open_func()
    {
        var group = Assert.Single(DysonA2uiPipelineRunCard.Build(1, Run(1, url: "javascript:alert(1)"), [], null, true, Now).Groups);

        Assert.Null(group.Run!.OpenFunc);
    }

    [Fact]
    public void Build_failures_become_a_message_and_never_throw()
    {
        Assert.Equal(DysonA2uiPipelineRunCard.NoRepo, DysonA2uiPipelineRunCard.Build(1, null, null, null, false, Now).Message);
        Assert.Equal("Run 7 not found", DysonA2uiPipelineRunCard.Build(7, null, null, null, true, Now).Message);

        foreach (var kind in Enum.GetValues<DysonPipelineErrorKind>())
        {
            var card = DysonA2uiPipelineRunCard.Build(7, null, null, new DysonPipelineError(kind, "boom"), true, Now);
            Assert.False(string.IsNullOrWhiteSpace(card.Message), kind.ToString());
            Assert.Empty(card.Groups);
        }
    }

    [Fact]
    public void BuildJobs_groups_by_run_in_id_order_and_ends_with_the_not_found_ids()
    {
        var found = new Dictionary<long, DysonPipelineJob>
        {
            [1] = Job(1, 100),
            [2] = Job(2, 200),
            [4] = Job(4, 100),
        };
        var runs = new Dictionary<long, DysonPipelineRun> { [100] = Run(100), [200] = Run(200) };

        var card = DysonA2uiPipelineRunCard.BuildJobs([1, 2, 3, 4], found, runs, null, true, Now);

        Assert.Null(card.Message);
        Assert.Equal(3, card.Groups.Count);
        Assert.Equal(100, card.Groups[0].Run!.RunId);
        Assert.Equal([1L, 4L], card.Groups[0].Jobs.Select(j => j.Id).ToArray());
        Assert.Equal(200, card.Groups[1].Run!.RunId);
        Assert.Null(card.Groups[2].Run);
        Assert.Equal("Job 3 not found", Assert.Single(card.Groups[2].Jobs).Message);
    }

    [Fact]
    public void BuildJobs_with_an_error_and_nothing_found_is_a_message()
    {
        var card = DysonA2uiPipelineRunCard.BuildJobs(
            [5], new Dictionary<long, DysonPipelineJob>(), new Dictionary<long, DysonPipelineRun>(),
            new DysonPipelineError(DysonPipelineErrorKind.CliMissing, "x"), true, Now);

        Assert.Equal("GitHub CLI (gh) is not installed", card.Message);
        Assert.Equal(DysonA2uiPipelineRunCard.NoRepo,
            DysonA2uiPipelineRunCard.BuildJobs([5], new Dictionary<long, DysonPipelineJob>(), new Dictionary<long, DysonPipelineRun>(), null, false, Now).Message);
    }

    [Fact]
    public void ParseRun_and_ParseJob_match_the_list_parsers()
    {
        const string runJson = """
            {"id":5,"name":"ci","display_title":"t","head_branch":"main","head_sha":"abcdef1234567","event":"push",
             "run_number":3,"run_attempt":2,"status":"completed","conclusion":"success",
             "created_at":"2026-07-26T11:00:00Z","run_started_at":"2026-07-26T11:01:00Z","updated_at":"2026-07-26T11:05:00Z",
             "html_url":"https://github.com/o/r/actions/runs/5"}
            """;
        const string jobJson = """
            {"id":9,"run_id":5,"name":"build","status":"in_progress","started_at":"2026-07-26T11:02:00Z",
             "html_url":"https://github.com/o/r/actions/runs/5/job/9"}
            """;

        using var runDoc = JsonDocument.Parse(runJson);
        using var jobDoc = JsonDocument.Parse(jobJson);
        var run = DysonGitHubVersionControlProvider.ParseRun(runDoc.RootElement);
        var job = DysonGitHubVersionControlProvider.ParseJob(jobDoc.RootElement, 0);

        Assert.Equal(run, DysonGitHubVersionControlProvider.ParseRuns($"{{\"workflow_runs\":[{runJson}]}}").Single());
        Assert.Equal(job, DysonGitHubVersionControlProvider.ParseJobs($"{{\"jobs\":[{jobJson}]}}", 0).Single());
        Assert.Equal(DysonPipelineState.Succeeded, run.State);
        Assert.Equal(2, run.Attempt);
        Assert.Equal(5, job.RunId);
        Assert.Equal(DysonPipelineState.Running, job.State);
    }
}
