using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Mid-turn model switch: <see cref="DysonAgentSession.SetModel"/> never touches the in-flight request; the next
/// provider request of the same turn resolves model, wire mode, chaining and context limit from the new pick.
/// </summary>
public class DysonMidTurnModelSwitchTests
{
    [Fact]
    public async Task Switch_during_request_applies_to_next_tool_round_without_cancelling_the_first()
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
            var providerA = Provider("gpt-a", "https://a.example/v1", DysonOpenAiApiModes.Completions);
            var providerB = Provider("gpt-b", "https://a.example/v1", DysonOpenAiApiModes.Completions, maxContext: 123_000);
            var session = new OpenAiCompatibleAgentSession(
                DysonAgentModes.Work, new DysonAgentSessionConfig(), providerA, http, workDir);

            var prompt = session.PromptAsync("# Task\n\ndo it");
            await handler.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(session.SetModel(providerB, providerB.DefaultMaxTargetContextTokens).IsSuccess);
            Assert.Same(providerA, session.Provider);
            Assert.Same(providerB, session.SelectedProvider);

            handler.ReleaseFirst.SetResult();
            var result = await prompt.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(result.IsSuccess, result.IsError ? result.Error : "");
            Assert.Equal(2, handler.Bodies.Count);
            Assert.Equal("gpt-a", JsonNode.Parse(handler.Bodies[0])!["model"]!.GetValue<string>());
            Assert.Equal("gpt-b", JsonNode.Parse(handler.Bodies[1])!["model"]!.GetValue<string>());
            Assert.False(handler.FirstRequestCancelled);
            Assert.Single(session.Turns[0].ToolCalls);
            Assert.Same(providerB, session.Provider);
            Assert.Equal(123_000, session.ResolveEffectiveMaxTargetContextTokens());
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public async Task Responses_switch_to_other_source_drops_chain_and_foreign_reasoning()
    {
        var workDir = CreateWorkDir();
        try
        {
            var handler = new GatedHandler(
            [
                () => Sse(
                    """{"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"NoSuchTool","arguments":"{}"}}""",
                    """{"type":"response.completed","response":{"id":"resp_1","output":[{"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"blob-a"},{"type":"function_call","id":"fc_1","call_id":"call_1","name":"NoSuchTool","arguments":"{}"}]}}"""),
                () => Sse(
                    """{"type":"response.output_text.delta","delta":"# Done\n\nok"}""",
                    """{"type":"response.completed","response":{"id":"resp_2","output":[]}}"""),
            ]);
            using var http = new HttpClient(handler);
            var providerA = Provider("gpt-a", "https://a.example/v1", DysonOpenAiApiModes.Responses);
            var providerB = Provider("gpt-b", "https://b.example/v1", DysonOpenAiApiModes.Responses);
            var session = new OpenAiCompatibleAgentSession(
                DysonAgentModes.Work, new DysonAgentSessionConfig(), providerA, http, workDir);

            var prompt = session.PromptAsync("# Task\n\ndo it");
            await handler.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(session.SetModel(providerB, null).IsSuccess);
            handler.ReleaseFirst.SetResult();
            var result = await prompt.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(result.IsSuccess, result.IsError ? result.Error : "");
            Assert.Equal(2, handler.Bodies.Count);
            var second = JsonNode.Parse(handler.Bodies[1])!;
            Assert.Equal("gpt-b", second["model"]!.GetValue<string>());
            // Same provider would have chained a delta on resp_1; a new source gets a full replay.
            Assert.Null(second["previous_response_id"]);
            Assert.DoesNotContain("blob-a", handler.Bodies[1], StringComparison.Ordinal);
            Assert.Contains("call_1", handler.Bodies[1], StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public void Idle_switch_applies_now_and_cross_kind_is_rejected()
    {
        var providerA = Provider("gpt-a", "https://a.example/v1", DysonOpenAiApiModes.Completions);
        var providerB = Provider("gpt-b", "https://a.example/v1", DysonOpenAiApiModes.Completions, maxContext: 50_000);
        using var http = new HttpClient();
        var session = new OpenAiCompatibleAgentSession(
            DysonAgentModes.Work, new DysonAgentSessionConfig(), providerA, http, Path.GetTempPath());

        Assert.True(session.SetModel(providerB, providerB.DefaultMaxTargetContextTokens).IsSuccess);
        Assert.Same(providerB, session.Provider);
        Assert.Same(providerB, session.SelectedProvider);
        Assert.Equal(50_000, session.SlugDefaultMaxTargetContextTokens);

        var crossKind = session.SetModel(new OtherProvider(), null);
        Assert.True(crossKind.IsError);
        Assert.Same(providerB, session.Provider);
    }

    [Fact]
    public async Task Concurrent_switches_settle_on_one_whole_selection()
    {
        using var http = new HttpClient();
        var initial = Provider("gpt-0", "https://a.example/v1", DysonOpenAiApiModes.Completions, maxContext: 0);
        var session = new OpenAiCompatibleAgentSession(
            DysonAgentModes.Work, new DysonAgentSessionConfig(), initial, http, Path.GetTempPath());
        var picks = Enumerable.Range(1, 64)
            .Select(i => Provider($"gpt-{i}", "https://a.example/v1", DysonOpenAiApiModes.Completions, maxContext: i))
            .ToArray();

        var scope = session.BeginInFlightPrompt(DysonAgentSession.CreateNormalTurn("x"));
        await Task.WhenAll(picks.Select(p => Task.Run(() => session.SetModel(p, p.DefaultMaxTargetContextTokens))));
        Assert.Same(initial, session.Provider);
        scope.Dispose();

        var applied = Assert.IsType<OpenAiCompatibleAgentProvider>(session.Provider);
        Assert.Contains(applied, picks);
        Assert.Equal(applied.DefaultMaxTargetContextTokens, session.SlugDefaultMaxTargetContextTokens);
        Assert.Same(applied, session.SelectedProvider);
    }

    private static OpenAiCompatibleAgentProvider Provider(string slug, string baseUrl, string apiMode, int? maxContext = null)
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
            Id = Guid.NewGuid(),
            ProviderId = entity.Id,
            Slug = slug,
            DisplayAlias = slug,
            DefaultMaxTargetContextTokens = maxContext,
            Provider = entity,
        });
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
        var dir = Path.Combine(Path.GetTempPath(), "dyson-midturn-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class OtherProvider : DysonAgentProvider;

    /// <summary>Holds the first POST open until released so the test can switch while it is in flight.</summary>
    private sealed class GatedHandler(IReadOnlyList<Func<HttpResponseMessage>> responses) : HttpMessageHandler
    {
        private int _index;

        public TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FirstRequestCancelled { get; private set; }
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var index = _index++;
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
