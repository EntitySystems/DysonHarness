using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// The 80% context notice: fires once per crossing at the same count and limit as the compaction trip, never below
/// the threshold, re-arms only on compaction, rides the next round (after the tool results) without forcing a turn.
/// </summary>
public class DysonContextWarningTests
{
    private const string Notice = "80% of 1,000 tokens context limit, compaction will likely occur soon";

    [Fact]
    public void Warning_FiresOnceAt80Percent()
    {
        var session = CreateSession(new HttpClient(), Path.GetTempPath());
        session.MaxTargetContextTokens = 1_000;
        var turn = NewTurn();

        session.ReportLiveContextTokens(turn, 799);
        Assert.False(session.TryDeliverContextWarning(turn));

        session.ReportLiveContextTokens(turn, 800);
        Assert.True(session.TryDeliverContextWarning(turn));

        session.ReportLiveContextTokens(turn, 900);
        Assert.False(session.TryDeliverContextWarning(turn));
        session.ReportLiveContextTokens(turn, 1_000);
        Assert.False(session.TryDeliverContextWarning(turn));

        var notice = Assert.Single(turn.ReasoningLog, s => s.Kind == DysonReasoningSegmentKind.HarnessNotice);
        Assert.Equal(Notice, notice.Text);
        Assert.Equal(DysonCompactionFlow.ContextWarningMarker, notice.Marker);
        Assert.Equal(0, notice.DeliveredAfterToolCalls);
        Assert.Equal("80% of 200,000 tokens context limit, compaction will likely occur soon",
            DysonCompactionFlow.FormatContextWarning(200_000));
    }

    [Fact]
    public async Task Warning_RearmsAfterCompaction()
    {
        var workDir = CreateWorkDir();
        try
        {
            var handler = new ScriptedHandler([() => Sse(TextChunk("one")), () => Sse(TextChunk("two"))]);
            using var http = new HttpClient(handler);
            var session = CreateSession(http, workDir);
            session.MaxTargetContextTokens = 0;
            Assert.True((await session.PromptAsync("# Task\n\nfirst")).IsSuccess);
            Assert.True((await session.PromptAsync("# Task\n\nsecond")).IsSuccess);
            session.MaxTargetContextTokens = 1_000;

            var turn = NewTurn();
            session.ReportLiveContextTokens(turn, 850);
            Assert.True(session.TryDeliverContextWarning(turn));
            session.ReportLiveContextTokens(turn, 850);
            Assert.False(session.TryDeliverContextWarning(turn));

            // Every compaction ends here: earlier turns excluded, so the notice re-arms.
            Assert.NotEmpty(DysonFullSummarizeFlow.ApplyAfterCompletion(session, session.Turns[1]));

            var next = NewTurn();
            session.ReportLiveContextTokens(next, 850);
            Assert.True(session.TryDeliverContextWarning(next));
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public void Warning_JumpPastLimit_TripsWithoutWarning()
    {
        var session = CreateSession(new HttpClient(), Path.GetTempPath());
        session.MaxTargetContextTokens = 1_000;
        var turn = NewTurn();

        session.ReportLiveContextTokens(turn, 100);
        session.ReportLiveContextTokens(turn, 1_500);

        Assert.True(session.IsCompactionRequested(turn));
        Assert.False(session.TryDeliverContextWarning(turn));
    }

    [Theory]
    [InlineData(DysonAgentModes.Explore, 1_000, 900)]
    [InlineData(DysonAgentModes.VoiceSidecar, 1_000, 110_000)]
    [InlineData(DysonAgentModes.Work, 0, 10_000_000)]
    public void Warning_NeverFor(string mode, int limit, int tokens)
    {
        var session = CreateSession(new HttpClient(), Path.GetTempPath(), mode);
        session.MaxTargetContextTokens = limit;
        var turn = NewTurn();

        session.ReportLiveContextTokens(turn, tokens);

        Assert.False(session.TryDeliverContextWarning(turn));
        Assert.DoesNotContain(turn.ReasoningLog, s => s.Kind == DysonReasoningSegmentKind.HarnessNotice);
    }

    [Fact]
    public void Warning_FullSummarizeTurn_NeitherArmsNorReceives()
    {
        var session = CreateSession(new HttpClient(), Path.GetTempPath());
        session.MaxTargetContextTokens = 1_000;
        var summary = new DysonAgentTurn { Kind = DysonAgentTurnKind.FullSummarize, StartedUtc = DateTime.UtcNow };

        session.ReportLiveContextTokens(summary, 900);
        Assert.False(session.TryDeliverContextWarning(NewTurn()));

        var normal = NewTurn();
        session.ReportLiveContextTokens(normal, 900);
        Assert.False(session.TryDeliverContextWarning(summary));
        Assert.True(session.TryDeliverContextWarning(normal));
    }

    [Fact]
    public void Warning_SegmentRoundTripsAndStaysOutOfSummaries()
    {
        var turn = NewTurn();
        turn.AppendHarnessNotice(DysonCompactionFlow.ContextWarningMarker, Notice);

        var restored = DysonReasoningLogSerializer.Deserialize(DysonReasoningLogSerializer.Serialize(turn.ReasoningLog));
        Assert.Equal(DysonReasoningSegmentKind.HarnessNotice, Assert.Single(restored).Kind);
        Assert.Single(DysonInjectedUserComments.FromTranscript(restored));
        Assert.Empty(DysonInjectedUserComments.From(restored));
        Assert.DoesNotContain(DysonCompactionFlow.ContextWarningMarker, DysonInjectedUserComments.FormatPlainText(restored));
    }

    [Fact]
    public async Task Notice_AppendedToNextRoundAfterToolResults_Completions()
    {
        var workDir = CreateWorkDir();
        try
        {
            var limit = 0;
            var handler = new ScriptedHandler(
            [
                () => Sse(
                    ToolCallChunk("call_1", "NoSuchSmokeTool", "{}"),
                    $$$"""{"id":"c1","choices":[],"usage":{"prompt_tokens":{{{limit * 85 / 100}}},"completion_tokens":0}}"""),
                () => Sse(ToolCallChunk("call_2", "NoSuchSmokeTool", "{}")),
                () => Sse(TextChunk("done")),
            ]);
            using var http = new HttpClient(handler);
            var session = CreateSession(http, workDir);
            limit = 2 * session.EstimateOutgoingContextTokens();
            session.MaxTargetContextTokens = limit;
            var expected = DysonCompactionFlow.ContextWarningMarker + "\n\n" + DysonCompactionFlow.FormatContextWarning(limit);

            var result = await session.PromptAsync("# Task\n\ndo the thing");

            Assert.True(result.IsSuccess, result.IsError ? result.Error : "");
            Assert.Equal(3, handler.PostCount);
            Assert.DoesNotContain(DysonCompactionFlow.ContextWarningMarker, handler.Bodies[0], StringComparison.Ordinal);

            var first = Messages(handler.Bodies[1]);
            var noticeAt = Assert.Single(Enumerable.Range(0, first.Count), i => Content(first[i]) == expected);
            var toolAt = Assert.Single(Enumerable.Range(0, first.Count),
                i => first[i]!["role"]?.GetValue<string>() == "tool"
                     && first[i]!["tool_call_id"]?.GetValue<string>() == "call_1");
            Assert.True(noticeAt > toolAt, $"notice {noticeAt} must follow the call_1 result {toolAt}");

            var second = Messages(handler.Bodies[2]);
            Assert.Single(second, m => Content(m) == expected);
            for (var i = 0; i < first.Count; i++)
                Assert.Equal(first[i]!.ToJsonString(), second[i]!.ToJsonString());
            Assert.False(session.TryDequeuePendingTurn(out _));
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public async Task Notice_OnFinalReply_RidesNextTurnWithoutQueuing()
    {
        var workDir = CreateWorkDir();
        try
        {
            var limit = 0;
            var handler = new ScriptedHandler(
            [
                () => Sse(
                    TextChunk("first done"),
                    $$$"""{"id":"c1","choices":[],"usage":{"prompt_tokens":{{{limit * 85 / 100}}},"completion_tokens":0}}"""),
                () => Sse(TextChunk("second done")),
            ]);
            using var http = new HttpClient(handler);
            var session = CreateSession(http, workDir);
            limit = 4 * session.EstimateOutgoingContextTokens();
            session.MaxTargetContextTokens = limit;

            Assert.True((await session.PromptAsync("# Task\n\nfirst")).IsSuccess);
            Assert.False(session.TryDequeuePendingTurn(out _));
            Assert.DoesNotContain(session.Turns[0].ReasoningLog, s => s.Kind == DysonReasoningSegmentKind.HarnessNotice);

            Assert.True((await session.PromptAsync("# Task\n\nsecond")).IsSuccess);

            Assert.Equal(2, handler.PostCount);
            var messages = Messages(handler.Bodies[1]);
            var noticeAt = Assert.Single(Enumerable.Range(0, messages.Count),
                i => Content(messages[i])?.StartsWith(DysonCompactionFlow.ContextWarningMarker, StringComparison.Ordinal) == true);
            Assert.Contains("second", Content(messages[noticeAt - 1]) ?? "", StringComparison.Ordinal);
            Assert.Equal(0, Assert.Single(session.Turns[1].ReasoningLog,
                s => s.Kind == DysonReasoningSegmentKind.HarnessNotice).DeliveredAfterToolCalls);
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    private static DysonAgentTurn NewTurn() => new() { Kind = DysonAgentTurnKind.Normal, StartedUtc = DateTime.UtcNow };

    private static JsonArray Messages(string body) => JsonNode.Parse(body)!["messages"]!.AsArray();

    private static string? Content(JsonNode? message) =>
        message?["content"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : message?["content"]?.ToJsonString();

    private static OpenAiCompatibleAgentSession CreateSession(HttpClient http, string workDir, string mode = DysonAgentModes.Work)
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
        var provider = new OpenAiCompatibleAgentProvider(
            entity,
            new DysonModelSlugEntity
            {
                Id = Guid.NewGuid(),
                ProviderId = entity.Id,
                Slug = "gpt-test",
                DisplayAlias = "gpt-test",
                Provider = entity,
            });
        return new OpenAiCompatibleAgentSession(mode, new DysonAgentSessionConfig(), provider, http, workDir);
    }

    private static string TextChunk(string jsonEscapedText) =>
        $$$"""{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant","content":"{{{jsonEscapedText}}}"}}]}""";

    private static string ToolCallChunk(string callId, string name, string args) =>
        $$$"""{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"{{{callId}}}","type":"function","function":{"name":"{{{name}}}","arguments":"{{{args}}}"}}]},"finish_reason":"tool_calls"}]}""";

    private static HttpResponseMessage Sse(params string[] chunks)
    {
        var sb = new StringBuilder();
        foreach (var chunk in chunks)
            sb.Append("data: ").Append(chunk).Append("\n\n");
        sb.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream"),
        };
    }

    private static string CreateWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dyson-ctxwarn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string workDir)
    {
        try
        {
            Directory.Delete(workDir, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    private sealed class ScriptedHandler(IReadOnlyList<Func<HttpResponseMessage>> responses) : HttpMessageHandler
    {
        private int _index;

        public int PostCount { get; private set; }

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            PostCount++;
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return _index < responses.Count
                ? responses[_index++]()
                : new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("no more scripted responses"),
                };
        }
    }
}
