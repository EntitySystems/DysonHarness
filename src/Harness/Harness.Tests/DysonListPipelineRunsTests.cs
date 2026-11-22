using System.Text.Json;

namespace DysonHarness.Tests;

/// <summary>ListPipelineRuns: snapshot reuse, job cap, per-run job failures, named errors, JSON shape. No gh, no network.</summary>
public sealed class DysonListPipelineRunsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Fresh_snapshot_costs_no_provider_call()
    {
        var provider = new FakeProvider { Runs = Runs(20) };
        await using var h = new Harness(provider);
        await h.WarmAsync();
        var before = provider.RunsCalls;

        var result = await h.CallAsync("{}");

        Assert.False(result.IsError, result.Content);
        using var doc = JsonDocument.Parse(result.Content);
        Assert.Equal(20, doc.RootElement.GetProperty("runs").GetArrayLength());
        Assert.Equal("o/r", doc.RootElement.GetProperty("repo").GetString());
        Assert.Equal(before, provider.RunsCalls);
    }

    [Fact]
    public async Task Missing_snapshot_costs_exactly_one_runs_call()
    {
        var provider = new FakeProvider { Runs = Runs(3) };
        await using var h = new Harness(provider);

        var result = await h.CallAsync("{}");

        Assert.False(result.IsError, result.Content);
        Assert.Equal(1, provider.RunsCalls);
        Assert.Equal(0, provider.JobsCalls);
        using var doc = JsonDocument.Parse(result.Content);
        var first = doc.RootElement.GetProperty("runs")[0];
        Assert.False(first.TryGetProperty("jobs", out _));
        foreach (var name in new[] { "id", "workflow", "title", "branch", "shortSha", "event", "runNumber", "attempt", "state", "stateLabel", "htmlUrl", "elapsed" })
            Assert.True(first.TryGetProperty(name, out _), name);
        Assert.Equal("abcdef1", first.GetProperty("shortSha").GetString());
        Assert.Equal("succeeded", first.GetProperty("state").GetString());
    }

    [Fact]
    public async Task IncludeJobs_fetches_at_most_ten_and_notes_the_rest()
    {
        var provider = new FakeProvider { Runs = Runs(15) };
        await using var h = new Harness(provider);

        var result = await h.CallAsync("""{"includeJobs":true}""");

        Assert.False(result.IsError, result.Content);
        Assert.Equal(DysonVersionControlService.MaxToolJobFetches, provider.JobsCalls);
        using var doc = JsonDocument.Parse(result.Content);
        var runs = doc.RootElement.GetProperty("runs").EnumerateArray().ToList();
        Assert.Equal(DysonVersionControlService.MaxToolJobFetches, runs.Count(r => r.GetProperty("jobs").ValueKind == JsonValueKind.Array));
        var noted = runs.Where(r => r.GetProperty("jobs").ValueKind == JsonValueKind.Null).ToList();
        Assert.Equal(5, noted.Count);
        Assert.All(noted, r => Assert.Equal(DysonPipelineToolJson.NotFetchedNote, r.GetProperty("jobsNote").GetString()));
    }

    [Fact]
    public async Task IncludeJobs_with_one_failing_run_notes_it_and_still_succeeds()
    {
        var provider = new FakeProvider
        {
            Runs = Runs(3),
            JobsError = id => id == 2 ? new DysonPipelineError(DysonPipelineErrorKind.Failed, "boom") : null,
        };
        await using var h = new Harness(provider);

        var result = await h.CallAsync("""{"includeJobs":true}""");

        Assert.False(result.IsError, result.Content);
        using var doc = JsonDocument.Parse(result.Content);
        var runs = doc.RootElement.GetProperty("runs").EnumerateArray().ToList();
        var failed = runs.Single(r => r.GetProperty("id").GetInt64() == 2);
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("jobs").ValueKind);
        Assert.Contains("boom", failed.GetProperty("jobsNote").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Array, runs.Single(r => r.GetProperty("id").GetInt64() == 1).GetProperty("jobs").ValueKind);
    }

    [Theory]
    [InlineData(DysonPipelineErrorKind.CliMissing, "GitHub CLI (gh) is not installed")]
    [InlineData(DysonPipelineErrorKind.NotAuthenticated, "gh is not signed in")]
    [InlineData(DysonPipelineErrorKind.RateLimited, "rate limit")]
    [InlineData(DysonPipelineErrorKind.RepoNotFound, "Repository not found")]
    public async Task Provider_errors_are_named_IsError_results(DysonPipelineErrorKind kind, string expected)
    {
        var provider = new FakeProvider { Error = new DysonPipelineError(kind, "x") };
        await using var h = new Harness(provider);

        var result = await h.CallAsync("{}");

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_repository_is_a_named_error()
    {
        var provider = new FakeProvider { Resolve = false };
        await using var h = new Harness(provider);

        var result = await h.CallAsync("{}");

        Assert.True(result.IsError);
        Assert.Contains("No GitHub repository", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, provider.RunsCalls);
    }

    [Fact]
    public async Task Null_delegate_means_pipelines_are_not_available()
    {
        var session = new StubSession(DysonAgentModes.MetaAgent);
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, Path.GetTempPath(), new HttpClient(), workDirectoryId: Guid.NewGuid());

        var result = await executor.ExecuteAsync(Call("{}"));

        Assert.True(result.IsError);
        Assert.Contains("Pipelines are not available in this host", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_boolean_includeJobs_is_an_error()
    {
        await using var h = new Harness(new FakeProvider());

        var result = await h.CallAsync("""{"includeJobs":"yes"}""");

        Assert.True(result.IsError);
        Assert.Contains("includeJobs", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolJson_elapsed_is_null_for_a_run_that_never_started()
    {
        var run = new DysonPipelineRun(
            1, "ci", "t", "main", "abcdef1234567", "push", 1, 1, DysonPipelineState.Queued, "Queued",
            T0, null, T0, "https://github.com/o/r/actions/runs/1");

        var json = DysonPipelineToolJson.Build(new DysonPipelineTarget("github.com", "o", "r", "https://github.com/o/r"), [run], null, T0);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("runs")[0].GetProperty("elapsed").ValueKind);
    }

    private static DysonToolCall Call(string args) => new()
    {
        CallId = Guid.NewGuid().ToString("N"),
        ToolName = "ListPipelineRuns",
        Stage = 1,
        ArgumentsJson = args,
    };

    private static List<DysonPipelineRun> Runs(int count) =>
        Enumerable.Range(1, count).Select(i => new DysonPipelineRun(
            i, "ci", "title", "main", "abcdef1234567", "push", i, 1, DysonPipelineState.Succeeded, "Succeeded",
            T0, T0, T0, $"https://github.com/o/r/actions/runs/{i}")).ToList();

    /// <summary>Real registry and service over a fake provider; the delay never fires, the clock is constant.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly DysonMessageBus _bus = new();
        private readonly DysonVersionControlRegistry _registry;
        private readonly DysonVersionControlService _service;
        private readonly StubSession _session = new(DysonAgentModes.MetaAgent);
        private readonly DysonWorkspaceToolExecutor _executor;
        private IDisposable? _lease;

        public Harness(FakeProvider provider)
        {
            var seams = new DysonVersionControlSeams(
                static (_, ct) => Task.Delay(Timeout.Infinite, ct),
                static () => T0,
                static (_, _) => Task.FromResult(Result<string, string>.AsValue("https://github.com/o/r.git")));
            _registry = new DysonVersionControlRegistry(_bus, [() => provider], seams);
            var entity = new DysonWorkDirectoryEntity
            {
                Id = Guid.NewGuid(), SubjectId = "local", AbsolutePath = "E:/repo", GitOrigin = "https://github.com/o/r.git",
            };
            _service = _registry.GetOrCreate(entity).Value;
            _session.Config.Pipelines = (_, _) => Task.FromResult(Result<DysonVersionControlService, string>.AsValue(_service));
            _executor = DysonWorkspaceTestFs.CreateExecutorAsync(
                _session, Path.GetTempPath(), new HttpClient(), workDirectoryId: entity.Id).GetAwaiter().GetResult();
        }

        /// <summary>A pill lease runs one tick; wait until it published, so the service holds a fresh snapshot.</summary>
        public async Task WarmAsync()
        {
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = _bus.Subscribe<DysonPipelinesChangedEvent>(
                DysonBusScopes.WorkDirectory(_service.WorkDirectoryId), _ => published.TrySetResult()).Value;
            _lease = _service.Watch(panelOpen: false);
            await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public Task<DysonToolCallResult> CallAsync(string args) => _executor.ExecuteAsync(Call(args));

        public ValueTask DisposeAsync()
        {
            _lease?.Dispose();
            _registry.Dispose();
            _bus.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeProvider : IVersionControlProvider
    {
        private int _runsCalls;
        private int _jobsCalls;

        public List<DysonPipelineRun> Runs { get; set; } = [];
        public DysonPipelineError? Error { get; set; }
        public bool Resolve { get; set; } = true;
        public Func<long, DysonPipelineError?>? JobsError { get; set; }
        public int RunsCalls => Volatile.Read(ref _runsCalls);
        public int JobsCalls => Volatile.Read(ref _jobsCalls);

        public string Id => "github";
        public string DisplayName => "GitHub";

        public Result<DysonPipelineTarget, DysonPipelineError> TryResolveTarget(string origin) =>
            Resolve
                ? Result<DysonPipelineTarget, DysonPipelineError>.AsValue(new DysonPipelineTarget("github.com", "o", "r", origin))
                : Result<DysonPipelineTarget, DysonPipelineError>.AsError(new DysonPipelineError(DysonPipelineErrorKind.NotDetected, "no"));

        public Task<Result<DysonPipelineFetch<DysonPipelineRun>, DysonPipelineError>> ListRunsAsync(
            DysonPipelineTarget target, string? etag, CancellationToken ct)
        {
            Interlocked.Increment(ref _runsCalls);
            return Task.FromResult(Error is { } error
                ? Result<DysonPipelineFetch<DysonPipelineRun>, DysonPipelineError>.AsError(error)
                : Result<DysonPipelineFetch<DysonPipelineRun>, DysonPipelineError>.AsValue(
                    new DysonPipelineFetch<DysonPipelineRun>(false, Runs, "e", 4000, null)));
        }

        public Task<Result<DysonPipelineFetch<DysonPipelineJob>, DysonPipelineError>> ListJobsAsync(
            DysonPipelineTarget target, long runId, string? etag, CancellationToken ct)
        {
            Interlocked.Increment(ref _jobsCalls);
            if (JobsError?.Invoke(runId) is { } error)
                return Task.FromResult(Result<DysonPipelineFetch<DysonPipelineJob>, DysonPipelineError>.AsError(error));
            IReadOnlyList<DysonPipelineJob> jobs =
            [
                new(runId * 10, runId, "build", DysonPipelineState.Succeeded, "Succeeded", T0, T0.AddSeconds(30), "https://github.com/o/r/job"),
            ];
            return Task.FromResult(Result<DysonPipelineFetch<DysonPipelineJob>, DysonPipelineError>.AsValue(
                new DysonPipelineFetch<DysonPipelineJob>(false, jobs, "j", null, null)));
        }

        public Task<Result<DysonPipelineRun, DysonPipelineError>> GetRunAsync(
            DysonPipelineTarget target, long runId, CancellationToken ct) => throw new NotSupportedException();

        public Task<Result<DysonPipelineJob, DysonPipelineError>> GetJobAsync(
            DysonPipelineTarget target, long jobId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode) : DysonAgentSession(
        mode,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
            IReadOnlyList<string>? contextFiles = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> LoadFunctionalContextAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
