using DysonHarness;

namespace Harness.Tests;

/// <summary>GitHub provider through fakes: no real gh process is ever started.</summary>
public class DysonGitHubVersionControlProviderTests
{
    private static readonly DysonPipelineTarget Target = new("github.com", "acme", "repo", "https://github.com/acme/repo");

    [Theory]
    [InlineData("https://github.com/acme/repo", "github.com", "acme", "repo")]
    [InlineData("https://github.com/acme/repo.git", "github.com", "acme", "repo")]
    [InlineData("https://github.com/acme/repo/", "github.com", "acme", "repo")]
    [InlineData("git@github.com:acme/repo.git", "github.com", "acme", "repo")]
    [InlineData("ssh://git@github.com/acme/repo", "github.com", "acme", "repo")]
    [InlineData("https://github.acme.com/acme/repo.git", "github.acme.com", "acme", "repo")]
    public void TryResolveTarget_parses_github_remotes(string origin, string host, string owner, string name)
    {
        var result = new DysonGitHubVersionControlProvider(static () => "gh", NeverRun).TryResolveTarget(origin);

        Assert.True(result.IsSuccess, result.IsError ? result.Error.Message : null);
        Assert.Equal(new DysonPipelineTarget(host, owner, name, $"https://{host}/{owner}/{name}"), result.Value);
    }

    [Theory]
    [InlineData("https://gitlab.com/acme/repo.git")]
    [InlineData("https://github.com/acme")]
    [InlineData("https://github.com/acme/repo/extra")]
    [InlineData("not a url")]
    public void TryResolveTarget_rejects_non_github_or_incomplete_remotes(string origin)
    {
        var result = new DysonGitHubVersionControlProvider(static () => "gh", NeverRun).TryResolveTarget(origin);

        Assert.True(result.IsError);
        Assert.Equal(DysonPipelineErrorKind.NotDetected, result.Error.Kind);
    }

    [Theory]
    [InlineData("HTTP/2.0 200 OK\r\nEtag: W/\"abc\"\r\nX-Ratelimit-Remaining: 12\r\n\r\n{\"ok\":true}\r\n")]
    [InlineData("HTTP/1.1 200 OK\nETag: W/\"abc\"\nX-Ratelimit-Remaining: 12\n\n{\"ok\":true}\n")]
    public void ParseHttp_splits_headers_and_body(string stdout)
    {
        Assert.True(DysonGitHubVersionControlProvider.ParseHttp(stdout, out var status, out var headers, out var body));
        Assert.Equal(200, status);
        Assert.Equal("W/\"abc\"", headers["etag"]);
        Assert.Equal("12", headers["X-RATELIMIT-REMAINING"]);
        Assert.Equal("{\"ok\":true}\n", body.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ParseHttp_rejects_output_without_a_status_line()
    {
        Assert.False(DysonGitHubVersionControlProvider.ParseHttp("not http\n\n", out _, out _, out _));
    }

    [Theory]
    [InlineData("queued", null, DysonPipelineState.Queued, "Queued")]
    [InlineData("requested", null, DysonPipelineState.Queued, "Queued")]
    [InlineData("pending", null, DysonPipelineState.Queued, "Queued")]
    [InlineData("waiting", null, DysonPipelineState.Waiting, "Waiting")]
    [InlineData("in_progress", null, DysonPipelineState.Running, "Running")]
    [InlineData("completed", "success", DysonPipelineState.Succeeded, "Succeeded")]
    [InlineData("completed", "failure", DysonPipelineState.Failed, "Failed")]
    [InlineData("completed", "timed_out", DysonPipelineState.Failed, "Timed out")]
    [InlineData("completed", "startup_failure", DysonPipelineState.Failed, "Startup failure")]
    [InlineData("completed", "cancelled", DysonPipelineState.Cancelled, "Cancelled")]
    [InlineData("completed", "skipped", DysonPipelineState.Skipped, "Skipped")]
    [InlineData("completed", "neutral", DysonPipelineState.Neutral, "Neutral")]
    [InlineData("completed", "stale", DysonPipelineState.Neutral, "Stale")]
    [InlineData("completed", "action_required", DysonPipelineState.Neutral, "Action required")]
    [InlineData("completed", "mystery", DysonPipelineState.Neutral, "mystery")]
    [InlineData("something_new", null, DysonPipelineState.Neutral, "something_new")]
    public void MapState_covers_every_status_and_conclusion(
        string status, string? conclusion, DysonPipelineState expected, string label)
    {
        Assert.Equal((expected, label), DysonGitHubVersionControlProvider.MapState(status, conclusion));
    }

    [Fact]
    public async Task ListRuns_maps_a_workflow_runs_payload()
    {
        const string body = """
            {"workflow_runs":[{"id":177,"name":"Continuous release","display_title":"Ship it",
            "head_branch":"dev","head_sha":"abcdef1234567890","event":"push","run_number":177,
            "run_attempt":2,"status":"completed","conclusion":null,"created_at":"2026-11-05T18:00:00Z",
            "run_started_at":"2026-11-05T18:01:00Z","updated_at":"2026-11-05T18:05:00Z",
            "html_url":"https://github.com/acme/repo/actions/runs/177"}]}
            """;
        var provider = Provider(Http(200, "ETag: W/\"runs\"\r\nX-Ratelimit-Remaining: 4581\r\nX-Ratelimit-Reset: 1700000000", body));

        var result = await provider.ListRunsAsync(Target, etag: null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsError ? result.Error.Message : null);
        var fetch = result.Value;
        Assert.False(fetch.NotModified);
        Assert.Equal("W/\"runs\"", fetch.ETag);
        Assert.Equal(4581, fetch.RateRemaining);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), fetch.RateReset);
        var run = Assert.Single(fetch.Items);
        Assert.Equal(177, run.Id);
        Assert.Equal("Continuous release", run.WorkflowName);
        Assert.Equal("Ship it", run.Title);
        Assert.Equal("dev", run.Branch);
        Assert.Equal("abcdef1234567890", run.HeadSha);
        Assert.Equal("push", run.Event);
        Assert.Equal(177, run.RunNumber);
        Assert.Equal(2, run.Attempt);
        Assert.Equal(DysonPipelineState.Neutral, run.State);
        Assert.Equal("completed", run.StateLabel);
        Assert.Equal(DateTimeOffset.Parse("2026-11-05T18:01:00Z"), run.StartedAt);
        Assert.Equal("https://github.com/acme/repo/actions/runs/177", run.HtmlUrl);
    }

    [Fact]
    public async Task ListJobs_maps_a_jobs_payload()
    {
        const string body = """
            {"jobs":[{"id":9,"run_id":177,"name":"build","status":"in_progress","conclusion":null,
            "started_at":"2026-11-05T18:02:00Z","completed_at":null,
            "html_url":"https://github.com/acme/repo/actions/runs/177/job/9"}]}
            """;
        var provider = Provider(Http(200, "ETag: W/\"jobs\"", body));

        var result = await provider.ListJobsAsync(Target, runId: 177, etag: null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsError ? result.Error.Message : null);
        var job = Assert.Single(result.Value.Items);
        Assert.Equal(9, job.Id);
        Assert.Equal(177, job.RunId);
        Assert.Equal("build", job.Name);
        Assert.Equal(DysonPipelineState.Running, job.State);
        Assert.Equal("Running", job.StateLabel);
        Assert.Null(job.CompletedAt);
        Assert.Equal("https://github.com/acme/repo/actions/runs/177/job/9", job.HtmlUrl);
    }

    [Fact]
    public async Task ListRuns_treats_exit_1_with_http_304_as_not_modified()
    {
        var provider = Provider(new DysonCliOutput(1, "HTTP/2.0 304 Not Modified\r\nETag: W/\"x\"\r\n\r\n", "gh: HTTP 304"));

        var result = await provider.ListRunsAsync(Target, "W/\"x\"", CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsError ? result.Error.Message : null);
        Assert.True(result.Value.NotModified);
        Assert.Empty(result.Value.Items);
        Assert.Equal("W/\"x\"", result.Value.ETag);
    }

    [Fact]
    public async Task ListRuns_401_is_not_authenticated()
    {
        var provider = Provider(new DysonCliOutput(1, "HTTP/2.0 401 Unauthorized\r\n\r\n", "gh auth login"));

        var result = await provider.ListRunsAsync(Target, null, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(DysonPipelineErrorKind.NotAuthenticated, result.Error.Kind);
        Assert.Contains("gh auth login --hostname github.com", result.Error.Message);
    }

    [Fact]
    public async Task ListRuns_403_with_rate_headers_is_rate_limited()
    {
        var stdout = "HTTP/2.0 403 Forbidden\r\nX-Ratelimit-Remaining: 0\r\nX-Ratelimit-Reset: 1700000100\r\n\r\n";
        var provider = Provider(new DysonCliOutput(1, stdout, "gh: HTTP 403"));

        var result = await provider.ListRunsAsync(Target, null, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(DysonPipelineErrorKind.RateLimited, result.Error.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000100), result.Error.RetryAfter);
    }

    [Fact]
    public async Task ListRuns_403_without_rate_headers_uses_the_stderr_first_line()
    {
        var stdout = "HTTP/2.0 403 Forbidden\r\n\r\n";
        var provider = Provider(new DysonCliOutput(1, stdout, "Resource protected by organization SAML enforcement.\nmore"));

        var result = await provider.ListRunsAsync(Target, null, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(DysonPipelineErrorKind.Failed, result.Error.Kind);
        Assert.Equal("Resource protected by organization SAML enforcement.", result.Error.Message);
    }

    [Fact]
    public async Task ListRuns_404_is_repo_not_found()
    {
        var provider = Provider(new DysonCliOutput(1, "HTTP/2.0 404 Not Found\r\n\r\n", "gh: Not Found (HTTP 404)"));

        var result = await provider.ListRunsAsync(Target, null, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(DysonPipelineErrorKind.RepoNotFound, result.Error.Kind);
        Assert.Equal("acme/repo not found, or the gh account has no access.", result.Error.Message);
    }

    [Fact]
    public async Task ListRuns_missing_gh_is_cli_missing_and_never_runs()
    {
        var calls = 0;
        var provider = new DysonGitHubVersionControlProvider(
            static () => null,
            (_, _) =>
            {
                calls++;
                return Task.FromResult(Result<DysonCliOutput, string>.AsError("should not run"));
            });

        var result = await provider.ListRunsAsync(Target, null, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(DysonPipelineErrorKind.CliMissing, result.Error.Kind);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ListRuns_passes_the_etag_as_one_argument_and_the_hostname()
    {
        IReadOnlyList<string>? seen = null;
        var provider = new DysonGitHubVersionControlProvider(
            static () => "gh",
            (args, _) =>
            {
                seen = args;
                return Task.FromResult(Result<DysonCliOutput, string>.AsValue(
                    new DysonCliOutput(1, "HTTP/2.0 304 Not Modified\r\n\r\n", "gh: HTTP 304")));
            });

        await provider.ListRunsAsync(Target, "W/\"abc\"", CancellationToken.None);

        Assert.NotNull(seen);
        Assert.Contains("--hostname", seen);
        Assert.Contains("github.com", seen);
        Assert.Contains("If-None-Match: W/\"abc\"", seen);
        Assert.Single(seen, arg => arg.Contains("If-None-Match", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListRuns_unparseable_output_asks_to_update_the_cli()
    {
        var provider = Provider(new DysonCliOutput(0, "Welcome to gh", ""));

        var result = await provider.ListRunsAsync(Target, null, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(DysonPipelineErrorKind.Failed, result.Error.Kind);
        Assert.Equal("Unexpected gh output; update GitHub CLI.", result.Error.Message);
    }

    private static DysonGitHubVersionControlProvider Provider(DysonCliOutput output) =>
        new(static () => "gh", (_, _) => Task.FromResult(Result<DysonCliOutput, string>.AsValue(output)));

    private static DysonCliOutput Http(int status, string headers, string body) =>
        new(0, $"HTTP/2.0 {status} OK\r\n{headers}\r\n\r\n{body}", "");

    private static Task<Result<DysonCliOutput, string>> NeverRun(IReadOnlyList<string> args, CancellationToken ct) =>
        throw new NotSupportedException();
}
