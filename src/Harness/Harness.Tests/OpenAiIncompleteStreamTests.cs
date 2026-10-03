using System.Net;
using System.Text;
using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// A model round that is cut off (stream ends with no terminal event, output-token limit, response.incomplete)
/// must not hand half-written tool arguments to a tool as "invalid JSON": the call gets ArgumentsError, and a cut
/// round with nothing to act on is a transient (retried) stream error. Fake SSE streams, no network.
/// </summary>
public class OpenAiIncompleteStreamTests
{
    private const string PartialArgs = "{\"planId\":59,\"title\":\"T\",\"summary\":\"S\"";

    [Fact]
    public async Task Responses_eof_mid_tool_call_flags_call_with_char_count_and_plan_hint()
    {
        var (reply, error) = await RunResponsesAsync(
            Event("response.created", """{"response":{"id":"resp_1"}}"""),
            ToolAdded("SubmitMetaPlan"),
            ArgsDelta(PartialArgs));

        Assert.Null(error);
        var call = Assert.Single(reply!.ToolCalls);
        Assert.Equal("call_1", call.CallId);
        Assert.Equal("{}", call.ArgumentsJson);
        Assert.NotNull(call.ArgumentsError);
        Assert.Contains($"cut off at {PartialArgs.Length} chars", call.ArgumentsError, StringComparison.Ordinal);
        Assert.Contains("stream ended before the model finished", call.ArgumentsError, StringComparison.Ordinal);
        Assert.Contains("EditMetaPlan edits[]", call.ArgumentsError, StringComparison.Ordinal);
        Assert.Equal("the stream ended before the model finished", reply.IncompleteReason);
    }

    [Fact]
    public async Task Responses_response_incomplete_flags_call_as_output_limit_with_generic_hint()
    {
        var (reply, error) = await RunResponsesAsync(
            ToolAdded("WriteFile"),
            ArgsDelta("{\"path\":\"a.txt\",\"content\":\"abc"),
            Event(
                "response.incomplete",
                """{"response":{"id":"resp_1","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[],"usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}}"""));

        Assert.Null(error);
        var call = Assert.Single(reply!.ToolCalls);
        Assert.Contains("output-token limit", call.ArgumentsError, StringComparison.Ordinal);
        Assert.Contains("split large content across several calls", call.ArgumentsError, StringComparison.Ordinal);
        Assert.DoesNotContain("EditMetaPlan", call.ArgumentsError, StringComparison.Ordinal);
        Assert.NotNull(reply.Usage);
    }

    [Fact]
    public async Task Responses_cut_stream_with_complete_and_partial_calls_flags_only_the_partial_one()
    {
        var (reply, error) = await RunResponsesAsync(
            ToolAdded("GetDateTime", itemId: "fc_0", callId: "call_0", outputIndex: 0),
            ArgsDelta("{\"stage\":0}", itemId: "fc_0", outputIndex: 0),
            ToolAdded("SubmitMetaPlan", itemId: "fc_1", callId: "call_1", outputIndex: 1),
            ArgsDelta(PartialArgs, itemId: "fc_1", outputIndex: 1));

        Assert.Null(error);
        Assert.Equal(2, reply!.ToolCalls.Count);
        Assert.Null(reply.ToolCalls[0].ArgumentsError);
        Assert.NotNull(reply.ToolCalls[1].ArgumentsError);
    }

    [Fact]
    public async Task Responses_cut_stream_without_tool_call_is_a_transient_error()
    {
        var (reply, error) = await RunResponsesAsync(
            Event("response.output_text.delta", """{"delta":"Hello"}"""));

        Assert.Null(reply);
        Assert.NotNull(error);
        Assert.StartsWith(OpenAiCompatibleHttp.StreamEndedErrorPrefix, error, StringComparison.Ordinal);
        Assert.Contains("5 chars received", error, StringComparison.Ordinal);
        Assert.True(OpenAiCompatibleHttp.IsTransientServerError(error));
    }

    [Fact]
    public async Task Responses_complete_stream_is_unchanged()
    {
        var args = "{\"planId\":59,\"stage\":2}";
        var (reply, error) = await RunResponsesAsync(
            ToolAdded("ReadMetaPlan"),
            ArgsDelta(args),
            Event(
                "response.completed",
                """{"response":{"id":"resp_1","output":[],"usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}}"""));

        Assert.Null(error);
        Assert.Null(reply!.IncompleteReason);
        var call = Assert.Single(reply.ToolCalls);
        Assert.Null(call.ArgumentsError);
        Assert.Equal(2, call.Stage);
        Assert.Equal("{\"planId\":59}", call.ArgumentsJson);
        Assert.NotNull(reply.Usage);
    }

    [Fact]
    public async Task Completions_finish_reason_length_flags_partial_call()
    {
        var (reply, error) = await RunCompletionsAsync(
            CompletionsChunk("""{"tool_calls":[{"index":0,"id":"toolu_1","type":"function","function":{"name":"SubmitMetaPlan","arguments":""}}]}"""),
            CompletionsChunk("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":" + JsonSerializer.Serialize(PartialArgs) + "}}]}"),
            CompletionsChunk("{}", finishReason: "length"),
            "data: [DONE]\n\n");

        Assert.Null(error);
        var call = Assert.Single(reply!.ToolCalls);
        Assert.Equal("toolu_1", call.CallId);
        Assert.Contains($"cut off at {PartialArgs.Length} chars", call.ArgumentsError, StringComparison.Ordinal);
        Assert.Contains("output-token limit", call.ArgumentsError, StringComparison.Ordinal);
        Assert.Equal("the model reached its output-token limit", reply.IncompleteReason);
    }

    [Fact]
    public async Task Completions_eof_mid_tool_call_flags_call_and_eof_without_tool_call_is_transient()
    {
        var (cut, cutError) = await RunCompletionsAsync(
            CompletionsChunk("""{"tool_calls":[{"index":0,"id":"toolu_1","type":"function","function":{"name":"SubmitMetaPlan","arguments":"{\"planId\":"}}]}"""));
        Assert.Null(cutError);
        Assert.Contains("stream ended before the model finished", Assert.Single(cut!.ToolCalls).ArgumentsError, StringComparison.Ordinal);

        var (reply, error) = await RunCompletionsAsync(CompletionsChunk("""{"content":"Hi"}"""));
        Assert.Null(reply);
        Assert.StartsWith(OpenAiCompatibleHttp.StreamEndedErrorPrefix, error, StringComparison.Ordinal);
        Assert.True(OpenAiCompatibleHttp.IsTransientServerError(error));
    }

    [Fact]
    public async Task Completions_complete_streams_are_unchanged_with_finish_reason_or_done_only()
    {
        var (withFinish, e1) = await RunCompletionsAsync(
            CompletionsChunk("""{"tool_calls":[{"index":0,"id":"toolu_1","type":"function","function":{"name":"GetDateTime","arguments":"{\"stage\":1}"}}]}"""),
            CompletionsChunk("{}", finishReason: "tool_calls"),
            "data: [DONE]\n\n");
        Assert.Null(e1);
        Assert.Null(withFinish!.IncompleteReason);
        var call = Assert.Single(withFinish.ToolCalls);
        Assert.Null(call.ArgumentsError);
        Assert.Equal(1, call.Stage);

        // Compat servers that omit finish_reason but send [DONE] still finish.
        var (doneOnly, e2) = await RunCompletionsAsync(CompletionsChunk("""{"content":"Hi"}"""), "data: [DONE]\n\n");
        Assert.Null(e2);
        Assert.Equal("Hi", doneOnly!.Content);
        Assert.Null(doneOnly.IncompleteReason);

        // Output limit with plain text keeps the truncated text (no retry, no tool call to flag).
        var (limited, e3) = await RunCompletionsAsync(
            CompletionsChunk("""{"content":"Half"}"""),
            CompletionsChunk("{}", finishReason: "length"));
        Assert.Null(e3);
        Assert.Equal("Half", limited!.Content);
        Assert.NotNull(limited.IncompleteReason);
    }

    [Fact]
    public async Task Scheduler_fails_a_call_with_arguments_error_without_running_the_tool()
    {
        var turn = new DysonAgentTurn();
        var cut = OpenAiCompatibleHttp.BuildToolCall("c1", "SubmitMetaPlan", PartialArgs, "the stream ended before the model finished");
        var ok = OpenAiCompatibleHttp.BuildToolCall("c2", "GetDateTime", "{}");
        turn.ToolCalls.Add(cut);
        turn.ToolCalls.Add(ok);
        var executed = new List<string>();

        var run = await DysonToolCallScheduler.RunStagedAsync(
            turn,
            (call, _) =>
            {
                executed.Add(call.CallId);
                return Task.FromResult(new DysonToolCallResult { CallId = call.CallId, ToolName = call.ToolName, Stage = call.Stage, Content = "ok" });
            });

        Assert.True(run.IsSuccess);
        Assert.Equal(new[] { "c2" }, executed);
        var failed = Assert.Single(turn.ResponseLog, r => r.CallId == "c1");
        Assert.True(failed.IsError);
        Assert.Equal(cut.ArgumentsError, failed.Content);
        Assert.Contains("cut off at", failed.Content, StringComparison.Ordinal);
    }

    // ---- fake SSE plumbing ----

    private static string Event(string type, string json) =>
        $"data: {{\"type\":\"{type}\",{json.TrimStart()[1..]}\n\n";

    private static string ToolAdded(string name, string itemId = "fc_1", string callId = "call_1", int outputIndex = 0) =>
        Event(
            "response.output_item.added",
            "{\"output_index\":" + outputIndex + ",\"item\":{\"type\":\"function_call\",\"id\":\"" + itemId
            + "\",\"call_id\":\"" + callId + "\",\"name\":\"" + name + "\",\"arguments\":\"\"}}");

    private static string ArgsDelta(string delta, string itemId = "fc_1", int outputIndex = 0) =>
        Event(
            "response.function_call_arguments.delta",
            "{\"item_id\":\"" + itemId + "\",\"output_index\":" + outputIndex + ",\"delta\":" + JsonSerializer.Serialize(delta) + "}");

    private static string CompletionsChunk(string delta, string? finishReason = null) =>
        $"data: {{\"id\":\"chatcmpl-1\",\"choices\":[{{\"index\":0,\"delta\":{delta},\"finish_reason\":{(finishReason is null ? "null" : JsonSerializer.Serialize(finishReason))}}}]}}\n\n";

    private static async Task<(OpenAiModelReply? Reply, string? Error)> RunResponsesAsync(params string[] sse)
    {
        var client = new OpenAiResponsesClient(new HttpClient(new SseHandler(string.Concat(sse))));
        var built = new OpenAiCacheFriendlyTranscriptBuilder.BuiltResponsesRequest("sys", [], [], "dyson:k:sp0", false, null, false);
        return await DrainAsync(client.StreamCreateAsync(CreateProvider(DysonOpenAiApiModes.Responses), built));
    }

    private static async Task<(OpenAiModelReply? Reply, string? Error)> RunCompletionsAsync(params string[] sse)
    {
        var client = new OpenAiCompletionsClient(new HttpClient(new SseHandler(string.Concat(sse))));
        var built = new OpenAiCacheFriendlyTranscriptBuilder.BuiltCompletionsRequest([], [], "dyson:k:sp0", false);
        return await DrainAsync(client.StreamCreateAsync(CreateProvider(DysonOpenAiApiModes.Completions), built));
    }

    private static async Task<(OpenAiModelReply? Reply, string? Error)> DrainAsync(
        IAsyncEnumerable<Result<OpenAiStreamChunk, string>> stream)
    {
        OpenAiModelReply? reply = null;
        await foreach (var chunk in stream)
        {
            if (chunk.IsError)
                return (null, chunk.Error);
            if (chunk.Value.CompletedReply is not null)
                reply = chunk.Value.CompletedReply;
        }

        return (reply, null);
    }

    private static OpenAiCompatibleAgentProvider CreateProvider(string apiMode)
    {
        var entity = new DysonModelProviderEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "P",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "http://127.0.0.1:8317/v1",
            ApiKey = "k",
            OpenAiApiMode = apiMode,
        };
        var slug = new DysonModelSlugEntity
        {
            Id = Guid.NewGuid(),
            ProviderId = entity.Id,
            Slug = "claude-test",
            Provider = entity,
        };
        return new OpenAiCompatibleAgentProvider(entity, slug);
    }

    private sealed class SseHandler(string sse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            });
    }
}
