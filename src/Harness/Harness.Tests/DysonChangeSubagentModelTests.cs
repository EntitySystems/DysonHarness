using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// <c>ChangeSubagentAIModel</c>: a fresh provider goes through <see cref="DysonAgentSession.SetModel"/>, so a busy
/// child keeps its in-flight request and lands the pick at the next provider request.
/// </summary>
public class DysonChangeSubagentModelTests
{
    [Fact]
    public async Task Busy_child_slug_and_effort_land_at_the_next_tool_round()
    {
        var workDir = CreateWorkDir();
        try
        {
            var handler = new GatedHandler(
            [
                () => Sse(
                    """{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"NoSuchTool","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""",
                    "[DONE]"),
                () => Sse(
                    """{"id":"c2","choices":[{"index":0,"delta":{"role":"assistant","content":"# Done\n\nok"}}]}""",
                    "[DONE]"),
            ]);
            using var http = new HttpClient(handler);
            var providerA = Provider("gpt-a", "https://a.example/v1", DysonOpenAiApiModes.Completions, effort: "high");
            var models = await SeedSlugsAsync();
            var child = new OpenAiCompatibleAgentSession(
                DysonAgentModes.Drone, new DysonAgentSessionConfig(), providerA, http, workDir, models: models);
            var parent = ParentOf(child, models);

            using var cts = new CancellationTokenSource();
            var prompt = child.PromptAsync("# Task\n\ndo it", cts.Token);
            await handler.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var changed = await parent.ChangeSubagentModelAsync(child.Id, "gpt-b", "low");
            Assert.True(changed.IsSuccess, changed.IsError ? changed.Error : "");
            Assert.Equal("nextToolRound", changed.Value.Applied);
            Assert.Same(providerA, child.Provider);
            Assert.Equal("gpt-b", ((OpenAiCompatibleAgentProvider)child.SelectedProvider).Slug);

            handler.ReleaseFirst.SetResult();
            await handler.SecondRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await StopAsync(cts, prompt);

            Assert.Equal("gpt-a", JsonNode.Parse(handler.Bodies[0])!["model"]!.GetValue<string>());
            Assert.Equal("high", JsonNode.Parse(handler.Bodies[0])!["reasoning_effort"]!.GetValue<string>());
            Assert.Equal("gpt-b", JsonNode.Parse(handler.Bodies[1])!["model"]!.GetValue<string>());
            Assert.Equal("low", JsonNode.Parse(handler.Bodies[1])!["reasoning_effort"]!.GetValue<string>());
            Assert.False(handler.FirstRequestCancelled);
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public async Task Effort_only_keeps_the_slug_and_lands_at_the_next_round()
    {
        var workDir = CreateWorkDir();
        try
        {
            var handler = new GatedHandler(
            [
                () => Sse(
                    """{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"NoSuchTool","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""",
                    "[DONE]"),
                () => Sse(
                    """{"id":"c2","choices":[{"index":0,"delta":{"role":"assistant","content":"# Done\n\nok"}}]}""",
                    "[DONE]"),
            ]);
            using var http = new HttpClient(handler);
            var providerA = Provider("gpt-a", "https://a.example/v1", DysonOpenAiApiModes.Completions, effort: "high");
            var child = new OpenAiCompatibleAgentSession(
                DysonAgentModes.Drone, new DysonAgentSessionConfig(), providerA, http, workDir);
            var parent = ParentOf(child);

            using var cts = new CancellationTokenSource();
            var prompt = child.PromptAsync("# Task\n\ndo it", cts.Token);
            await handler.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var changed = await parent.ChangeSubagentModelAsync(child.Id, null, "low");
            Assert.True(changed.IsSuccess, changed.IsError ? changed.Error : "");
            Assert.Equal("nextToolRound", changed.Value.Applied);

            handler.ReleaseFirst.SetResult();
            await handler.SecondRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await StopAsync(cts, prompt);

            Assert.Equal("gpt-a", JsonNode.Parse(handler.Bodies[1])!["model"]!.GetValue<string>());
            Assert.Equal("low", JsonNode.Parse(handler.Bodies[1])!["reasoning_effort"]!.GetValue<string>());
            Assert.Equal(providerA.SlugId, ((OpenAiCompatibleAgentProvider)child.Provider).SlugId);
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public async Task Idle_child_applies_now_and_persists_the_row()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        using var _ = connection;
        var sessions = DysonTempDb.Sessions(accessor);
        var models = DysonTempDb.Models(accessor);
        var providerRow = await models.CreateProviderAsync(new DysonModelProviderEntity
        {
            DisplayName = "test",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "https://a.example/v1",
            ApiKey = "sk-test",
            OpenAiApiMode = DysonOpenAiApiModes.Completions,
        });
        var slugA = await models.AddSlugAsync(providerRow.Value, "gpt-a", "gpt-a", defaultReasoningEffort: "high");
        var slugB = await models.AddSlugAsync(providerRow.Value, "gpt-b", "gpt-b", defaultReasoningEffort: "low");

        using var http = new HttpClient();
        var providerA = Provider("gpt-a", "https://a.example/v1", DysonOpenAiApiModes.Completions, effort: "high", slugId: slugA.Value);
        var child = new OpenAiCompatibleAgentSession(
            DysonAgentModes.Drone, new DysonAgentSessionConfig(), providerA, http, Path.GetTempPath(), sessions, models: models);
        var parent = ParentOf(child, models);
        var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = child.Id,
            AgentMode = DysonAgentModes.Drone,
            ModelSlugId = slugA.Value,
            ReasoningEffort = "high",
            McpAccessMode = DysonMcpAccessMode.FullAccess,
            Title = "child",
            SystemPromptSnapshot = "p",
            Status = DysonSessionStatus.Active,
        });
        InvokeSetPersistenceId(child, created.Value);

        var changed = await parent.ChangeSubagentModelAsync(child.Id, "gpt-b", null);
        Assert.True(changed.IsSuccess, changed.IsError ? changed.Error : "");
        Assert.Equal("now", changed.Value.Applied);
        Assert.Equal("gpt-b", ((OpenAiCompatibleAgentProvider)child.Provider).Slug);
        Assert.Equal("low", ((OpenAiCompatibleAgentProvider)child.Provider).ReasoningEffort);

        var row = await sessions.GetFullSessionAsync(created.Value);
        Assert.Equal(slugB.Value, row.Value!.Session.ModelSlugId);
        Assert.Equal("low", row.Value.Session.ReasoningEffort);
    }

    [Fact]
    public async Task Refusals_cover_unknown_id_stopped_cross_kind_and_neither_param()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        using var _ = connection;
        var models = DysonTempDb.Models(accessor);
        var demoProvider = await models.CreateProviderAsync(new DysonModelProviderEntity
        {
            DisplayName = "demo",
            ProviderKind = DysonProviderKinds.Demo,
        });
        await models.AddSlugAsync(demoProvider.Value, "demo-only", "demo-only");

        using var http = new HttpClient();
        var providerA = Provider("gpt-a", "https://a.example/v1", DysonOpenAiApiModes.Completions);
        var child = new OpenAiCompatibleAgentSession(
            DysonAgentModes.Drone, new DysonAgentSessionConfig(), providerA, http, Path.GetTempPath(), models: models);
        var parent = ParentOf(child, models);
        var unknown = await parent.ChangeSubagentModelAsync(999, "gpt-a", null);
        Assert.True(unknown.IsError);
        Assert.Contains("Unknown subagentId", unknown.Error, StringComparison.Ordinal);

        var neither = await parent.ChangeSubagentModelAsync(child.Id, null, null);
        Assert.True(neither.IsError);
        Assert.Contains("required", neither.Error, StringComparison.Ordinal);

        var crossKind = await parent.ChangeSubagentModelAsync(child.Id, "demo-only", null);
        Assert.True(crossKind.IsError);
        Assert.Contains("not OpenAI-compatible", crossKind.Error, StringComparison.Ordinal);

        child.TryMarkTerminal(DysonSessionStatus.Stopped, "stopped");
        var stopped = await parent.ChangeSubagentModelAsync(child.Id, null, "low");
        Assert.True(stopped.IsError);
        Assert.Contains("stopped", stopped.Error, StringComparison.Ordinal);
        Assert.Same(providerA, child.Provider);
    }

    [Fact]
    public async Task Same_pick_is_unchanged_and_writes_nothing()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        using var _ = connection;
        var sessions = DysonTempDb.Sessions(accessor);
        var models = DysonTempDb.Models(accessor);
        var providerRow = await models.CreateProviderAsync(new DysonModelProviderEntity
        {
            DisplayName = "test",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "https://a.example/v1",
            ApiKey = "sk-test",
            OpenAiApiMode = DysonOpenAiApiModes.Completions,
        });
        var slugA = await models.AddSlugAsync(providerRow.Value, "gpt-a", "gpt-a", defaultReasoningEffort: "high");

        using var http = new HttpClient();
        var providerA = Provider("gpt-a", "https://a.example/v1", DysonOpenAiApiModes.Completions, effort: "high", slugId: slugA.Value);
        var child = new OpenAiCompatibleAgentSession(
            DysonAgentModes.Drone, new DysonAgentSessionConfig(), providerA, http, Path.GetTempPath(), sessions, models: models);
        var parent = ParentOf(child, models);
        var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = child.Id,
            AgentMode = DysonAgentModes.Drone,
            ModelSlugId = slugA.Value,
            ReasoningEffort = "high",
            McpAccessMode = DysonMcpAccessMode.FullAccess,
            Title = "child",
            SystemPromptSnapshot = "p",
            Status = DysonSessionStatus.Active,
        });
        InvokeSetPersistenceId(child, created.Value);

        var changed = await parent.ChangeSubagentModelAsync(child.Id, "gpt-a", "high");
        Assert.True(changed.IsSuccess, changed.IsError ? changed.Error : "");
        Assert.Equal("unchanged", changed.Value.Applied);
        Assert.Same(providerA, child.Provider);
    }

    [Fact]
    public async Task ListSubagents_shows_the_pending_label_while_busy()
    {
        var workDir = CreateWorkDir();
        try
        {
            var handler = new GatedHandler(
            [
                () => Sse(
                    """{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant","content":"# Done\n\nok"}}]}""",
                    "[DONE]"),
            ]);
            using var http = new HttpClient(handler);
            var models = await SeedSlugsAsync();
            var providerA = Provider("gpt-a", "https://a.example/v1", DysonOpenAiApiModes.Completions);
            var child = new OpenAiCompatibleAgentSession(
                DysonAgentModes.Drone, new DysonAgentSessionConfig(), providerA, http, workDir, models: models);
            var parent = ParentOf(child, models);

            using var cts = new CancellationTokenSource();
            var prompt = child.PromptAsync("# Task\n\ndo it", cts.Token);
            await handler.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var changed = await parent.ChangeSubagentModelAsync(child.Id, "gpt-b", "low");
            Assert.True(changed.IsSuccess, changed.IsError ? changed.Error : "");

            Assert.Contains("gpt-b", parent.FormatListSubagentsJson(), StringComparison.Ordinal);
            Assert.DoesNotContain("gpt-a", parent.FormatListSubagentsJson(), StringComparison.Ordinal);

            handler.ReleaseFirst.SetResult();
            await StopAsync(cts, prompt);
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    private static readonly List<SqliteConnection> SeedConnections = [];

    private static async Task<IDysonModelRepository> SeedSlugsAsync()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        SeedConnections.Add(connection);
        var models = DysonTempDb.Models(accessor);
        var providerRow = await models.CreateProviderAsync(new DysonModelProviderEntity
        {
            DisplayName = "test",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "https://a.example/v1",
            ApiKey = "sk-test",
            OpenAiApiMode = DysonOpenAiApiModes.Completions,
        });
        await models.AddSlugAsync(providerRow.Value, "gpt-a", "gpt-a", defaultReasoningEffort: "high");
        await models.AddSlugAsync(providerRow.Value, "gpt-b", "gpt-b", defaultReasoningEffort: "low");
        return models;
    }

    private static void InvokeSetPersistenceId(DysonAgentSession session, Guid id) =>
        typeof(DysonAgentSession).GetMethod("SetPersistenceId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [id]);

    /// <summary>A registered child runs as a subagent (report mandate), so tests stop it once the bodies they need landed.</summary>
    private static async Task StopAsync(CancellationTokenSource cts, Task prompt)
    {
        await cts.CancelAsync();
        try { await prompt.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { /* cancelled on purpose */ }
    }

    private static TestParent ParentOf(OpenAiCompatibleAgentSession child, IDysonModelRepository? models = null)
    {
        var parent = new TestParent(models);
        parent.RegisterForTest(child);
        return parent;
    }

    /// <summary>
    /// A parent only resolves a slug through the OpenAI-compatible override, so this stub forwards to one.
    /// </summary>
    private sealed class TestParent(IDysonModelRepository? models) : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        ParentProvider())
    {
        private readonly OpenAiCompatibleAgentSession _resolver = new(
            DysonAgentModes.Work, new DysonAgentSessionConfig(), ParentProvider(),
            new HttpClient(), Path.GetTempPath(), models: models);

        public void RegisterForTest(DysonAgentSession child) => RegisterSubagent(child);

        internal override Task<Result<OpenAiCompatibleAgentProvider, string>> ResolveSwitchProviderAsync(
            OpenAiCompatibleAgentProvider current, string? modelSlug, string? reasoningEffort,
            CancellationToken cancellationToken) =>
            _resolver.ResolveSwitchProviderAsync(current, modelSlug, reasoningEffort, cancellationToken);

        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode, string task, string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null, string? reasoningEffort = null,
            IReadOnlyList<string>? contextFiles = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> LoadFunctionalContextAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(string prompt, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(string prompt, IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(DysonAgentTurn turn, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(string planRelativePath, IReadOnlyList<string>? reportBlocks = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(DysonAgentInterrupt interrupt, string? title = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(string instruction, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptShellExitedAsync(DysonAgentInterrupt interrupt, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static OpenAiCompatibleAgentProvider ParentProvider() =>
        Provider("parent", "https://a.example/v1", DysonOpenAiApiModes.Completions);

    private static OpenAiCompatibleAgentProvider Provider(
        string slug, string baseUrl, string apiMode, string? effort = null, Guid? slugId = null, int? maxContext = null)
    {
        var entity = new DysonModelProviderEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "test",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = baseUrl,
            ApiKey = "sk-test",
            OpenAiApiMode = apiMode,
        };
        return new OpenAiCompatibleAgentProvider(entity, new DysonModelSlugEntity
        {
            Id = slugId ?? Guid.NewGuid(),
            ProviderId = entity.Id,
            Slug = slug,
            DisplayAlias = slug,
            DefaultReasoningEffort = effort,
            DefaultMaxTargetContextTokens = maxContext,
            Provider = entity,
        }, effort);
    }

    private static HttpResponseMessage Sse(params string[] chunks)
    {
        var sb = new StringBuilder();
        foreach (var chunk in chunks)
            sb.Append("data: ").Append(chunk).Append("\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream"),
        };
    }

    private static string CreateWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dyson-change-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Holds the first POST open until released so the test can switch while it is in flight.</summary>
    private sealed class GatedHandler(IReadOnlyList<Func<HttpResponseMessage>> responses) : HttpMessageHandler
    {
        private int _index;

        public TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FirstRequestCancelled { get; private set; }
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var index = _index++;
            if (index == 1)
                SecondRequestStarted.TrySetResult();
            if (index == 0)
            {
                FirstRequestStarted.TrySetResult();
                await ReleaseFirst.Task;
                FirstRequestCancelled = cancellationToken.IsCancellationRequested;
            }

            return index < responses.Count
                ? responses[index]()
                : new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("no more scripted responses"),
                };
        }
    }
}
