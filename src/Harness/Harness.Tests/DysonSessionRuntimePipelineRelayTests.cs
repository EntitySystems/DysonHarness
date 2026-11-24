using System.Reflection;
using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Sessions the retained runtime builds carry no <c>Config.Pipelines</c> hook, so the relay must resolve the work
/// directory's pipelines service from the runtime's own registry and repository (otherwise a Meta session never gets events).
/// </summary>
public class DysonSessionRuntimePipelineRelayTests
{
    [Fact]
    public async Task Live_meta_session_without_a_pipelines_hook_gets_an_events_lease_from_the_runtime_registry()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        var dir = Path.Combine(Path.GetTempPath(), "dyson-relay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var http = new HttpClient();
        using var bus = new DysonMessageBus();
        using var registry = new DysonVersionControlRegistry(
            bus, [() => new QuietProvider()],
            new DysonVersionControlSeams(
                static (_, ct) => Task.Delay(TimeSpan.FromMilliseconds(20), ct),
                static () => DateTimeOffset.UtcNow,
                static (_, _) => Task.FromResult(Result<string, string>.AsValue("https://github.com/o/r.git"))));
        try
        {
            var subject = DysonTempDb.Subject();
            var sessions = DysonTempDb.Sessions(accessor, subject);
            var workDirectories = DysonTempDb.WorkDirectories(accessor, subject);
            var wd = await workDirectories.CreateAsync(dir);
            Assert.True(wd.IsSuccess, wd.IsError ? wd.Error : null);

            await using var runtime = new DysonSessionRuntime(
                subject, sessions, new NoFactory(), bus: bus, versionControl: registry, workDirectories: workDirectories);
            var row = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
            {
                RuntimeId = 0,
                AgentMode = DysonAgentModes.MetaAgent,
                WorkDirectoryId = wd.Value,
                Title = "meta",
                SystemPromptSnapshot = "meta",
            });
            Assert.True(row.IsSuccess, row.IsError ? row.Error : null);
            var session = new OpenAiCompatibleAgentSession(
                DysonAgentModes.MetaAgent, new DysonAgentSessionConfig { Bus = bus }, Provider(), http, dir,
                workDirectoryId: wd.Value);
            typeof(DysonAgentSession).GetMethod("SetPersistenceId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(session, [row.Value]);
            Assert.Null(session.Config.Pipelines);
            Assert.True(runtime.AdoptLiveSession(session).IsSuccess);

            await runtime.SyncPipelineRelayAsync();

            Assert.True(registry.TryGet(wd.Value, out var service));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (service.Snapshot is null && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.NotNull(service.Snapshot);
        }
        finally
        {
            await connection.DisposeAsync();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static OpenAiCompatibleAgentProvider Provider()
    {
        var entity = new DysonModelProviderEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "test",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-test",
            OpenAiApiMode = DysonOpenAiApiModes.Completions,
        };
        return new OpenAiCompatibleAgentProvider(
            entity,
            new DysonModelSlugEntity
            {
                Id = Guid.NewGuid(),
                ProviderId = entity.Id,
                Slug = "gpt-test",
                DisplayAlias = "gpt-test",
                Provider = entity,
            });
    }

    private sealed class NoFactory : IDysonAgentSessionRuntimeFactory
    {
        public Task<Result<DysonAgentSessionRuntimeLease, string>> CreateRootAsync(
            DysonAgentSessionRuntimeCreateRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<DysonAgentSessionRuntimeLease, string>> LoadAsync(
            Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class QuietProvider : IVersionControlProvider
    {
        public string Id => "github";
        public string DisplayName => "GitHub";

        public Result<DysonPipelineTarget, DysonPipelineError> TryResolveTarget(string origin) =>
            Result<DysonPipelineTarget, DysonPipelineError>.AsValue(new DysonPipelineTarget("github.com", "o", "r", origin));

        public Task<Result<DysonPipelineFetch<DysonPipelineRun>, DysonPipelineError>> ListRunsAsync(
            DysonPipelineTarget target, string? etag, CancellationToken ct) =>
            Task.FromResult(Result<DysonPipelineFetch<DysonPipelineRun>, DysonPipelineError>.AsValue(
                new DysonPipelineFetch<DysonPipelineRun>(false, [], null, 4000, null)));

        public Task<Result<DysonPipelineFetch<DysonPipelineJob>, DysonPipelineError>> ListJobsAsync(
            DysonPipelineTarget target, long runId, string? etag, CancellationToken ct) =>
            Task.FromResult(Result<DysonPipelineFetch<DysonPipelineJob>, DysonPipelineError>.AsValue(
                new DysonPipelineFetch<DysonPipelineJob>(false, [], null, null, null)));

        public Task<Result<DysonPipelineRun, DysonPipelineError>> GetRunAsync(
            DysonPipelineTarget target, long runId, CancellationToken ct) =>
            Task.FromResult(Result<DysonPipelineRun, DysonPipelineError>.AsError(
                new DysonPipelineError(DysonPipelineErrorKind.RepoNotFound, "404")));

        public Task<Result<DysonPipelineJob, DysonPipelineError>> GetJobAsync(
            DysonPipelineTarget target, long jobId, CancellationToken ct) =>
            Task.FromResult(Result<DysonPipelineJob, DysonPipelineError>.AsError(
                new DysonPipelineError(DysonPipelineErrorKind.RepoNotFound, "404")));
    }
}
