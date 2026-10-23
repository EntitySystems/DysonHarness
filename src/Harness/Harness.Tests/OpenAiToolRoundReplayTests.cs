using System.Text;
using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Completed turns with recorded round counts replay the same tool blocks as the in-flight
/// rebuild. Responses reasoning from a finished turn stays off. Legacy turns stay one chunk.
/// </summary>
public class OpenAiToolRoundReplayTests
{
    [Fact]
    public void Run()
    {
        AssertRecordedRoundsMatchInFlightAndDropReasoning();
        AssertLegacyTurnStaysOneCompletionsChunk();
        AssertRoundCountsRoundTrip();
    }

    private static void AssertRecordedRoundsMatchInFlightAndDropReasoning()
    {
        const string reasoningMarker = "SECRET_REASONING_BLOB";
        const string comment = "steer after the first round";
        var calls = new List<DysonToolCall>
        {
            Call("c1", "Grep"),
            Call("c2", "ReadFile"),
            Call("c3", "Grep"),
        };
        var results = new List<DysonToolCallResult>
        {
            Result("c1", "Grep", "one"),
            Result("c2", "ReadFile", "two"),
            Result("c3", "Grep", "three"),
        };

        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "do the tools",
            StartedUtc = DateTime.UtcNow,
        };
        turn.ToolCalls.AddRange(calls);
        foreach (var result in results)
            turn.ResponseLog.Enqueue(result);
        turn.ToolRoundCallCounts.Add(2);
        turn.ToolRoundCallCounts.Add(1);
        turn.RestoreReasoningLog(
        [
            new DysonReasoningSegment(
                DysonReasoningSegmentKind.UserComment,
                comment,
                RoundIndex: 0,
                DeliveredAfterToolCalls: 2),
        ]);

        var session = new StubSession();
        session.AddTurnForTest(turn);

        var reasoning = new JsonObject
        {
            ["type"] = "reasoning",
            ["id"] = "rs_test",
            ["encrypted_content"] = reasoningMarker,
        };
        IReadOnlyList<OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound> rounds =
        [
            new(
                calls.GetRange(0, 2),
                results.GetRange(0, 2),
                [reasoning]),
            new(calls.GetRange(2, 1), results.GetRange(2, 1)),
        ];

        var inflightCompletions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session, currentUserPrompt: null, currentFilePaths: null, rounds);
        var inflightResponses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, currentUserPrompt: null, currentFilePaths: null, rounds);

        turn.AssistantText = "done";
        turn.CompletedUtc = DateTime.UtcNow;

        var doneCompletions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session, currentUserPrompt: null, currentFilePaths: null);
        var doneResponses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, currentUserPrompt: null, currentFilePaths: null);

        var liveTools = ToolRegionCompletions(inflightCompletions.Messages);
        var doneTools = ToolRegionCompletions(doneCompletions.Messages);
        if (!string.Equals(liveTools, doneTools, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Completions tool region diverged.\nLive: {liveTools}\nDone: {doneTools}");
        }

        var liveResponses = ToolRegionResponses(inflightResponses.Input);
        var doneResponsesRegion = ToolRegionResponses(doneResponses.Input);
        if (!string.Equals(liveResponses, doneResponsesRegion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Responses tool region diverged.\nLive: {liveResponses}\nDone: {doneResponsesRegion}");
        }

        var doneJson = doneResponses.Input.ToJsonString();
        if (doneJson.Contains(reasoningMarker, StringComparison.Ordinal))
            throw new InvalidOperationException("Completed Responses must omit finished-turn reasoning.");

        if (!inflightResponses.Input.ToJsonString().Contains(reasoningMarker, StringComparison.Ordinal))
            throw new InvalidOperationException("In-flight Responses must still carry the reasoning item.");

        if (!doneTools.Contains(comment, StringComparison.Ordinal)
            || doneTools.IndexOf("\"c2\"", StringComparison.Ordinal) > doneTools.IndexOf(comment, StringComparison.Ordinal)
            || doneTools.IndexOf(comment, StringComparison.Ordinal) > doneTools.IndexOf("\"c3\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Comment anchored at the first round must sit between round 1 and round 2.");
        }
    }

    private static void AssertLegacyTurnStaysOneCompletionsChunk()
    {
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "legacy",
            AssistantText = "done",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };
        turn.ToolCalls.Add(Call("a", "Grep"));
        turn.ToolCalls.Add(Call("b", "ReadFile"));
        turn.ToolCalls.Add(Call("c", "Grep"));
        turn.ResponseLog.Enqueue(Result("a", "Grep", "1"));
        turn.ResponseLog.Enqueue(Result("b", "ReadFile", "2"));
        turn.ResponseLog.Enqueue(Result("c", "Grep", "3"));

        var session = new StubSession();
        session.AddTurnForTest(turn);
        var built = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session, currentUserPrompt: null, currentFilePaths: null);

        var chunks = 0;
        var callsInChunk = 0;
        foreach (var node in built.Messages)
        {
            if (node is not JsonObject obj || obj["tool_calls"] is not JsonArray toolCalls)
                continue;

            chunks++;
            callsInChunk = toolCalls.Count;
        }

        if (chunks != 1 || callsInChunk != 3)
        {
            throw new InvalidOperationException(
                $"Legacy turn must emit one tool_calls array of 3, got {chunks} chunk(s) / {callsInChunk} call(s).");
        }
    }

    private static void AssertRoundCountsRoundTrip()
    {
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            AssistantText = "done",
        };
        turn.ToolRoundCallCounts.Add(2);
        turn.ToolRoundCallCounts.Add(1);
        var json = DysonTurnToolStateSerializer.CaptureFromTurn(turn);
        var restored = new DysonAgentTurn();
        DysonTurnToolStateSerializer.ApplyToTurn(restored, json);
        if (restored.ToolRoundCallCounts.Count != 2
            || restored.ToolRoundCallCounts[0] != 2
            || restored.ToolRoundCallCounts[1] != 1)
        {
            throw new InvalidOperationException(
                $"Round counts must round-trip, got [{string.Join(',', restored.ToolRoundCallCounts)}].");
        }

        var legacy = new DysonAgentTurn();
        DysonTurnToolStateSerializer.ApplyToTurn(
            legacy,
            """{"toolCalls":[],"tracked":[],"responseLog":[]}""");
        if (legacy.ToolRoundCallCounts.Count != 0)
            throw new InvalidOperationException("Old tool-state rows must deserialize with empty round counts.");
    }

    private static string ToolRegionCompletions(JsonArray messages)
    {
        var sb = new StringBuilder();
        foreach (var node in messages)
        {
            if (node is not JsonObject obj)
                continue;

            var role = obj["role"]?.GetValue<string>();
            if (string.Equals(role, "assistant", StringComparison.Ordinal) && obj["tool_calls"] is not null)
                sb.Append(obj["tool_calls"]!.ToJsonString());
            else if (string.Equals(role, "tool", StringComparison.Ordinal))
                sb.Append(obj.ToJsonString());
            else if (string.Equals(role, "user", StringComparison.Ordinal)
                     && obj["content"]?.GetValue<string>() is { } text
                     && text.Contains(DysonInjectedUserComments.Marker, StringComparison.Ordinal))
                sb.Append(text);
        }

        return sb.ToString();
    }

    private static string ToolRegionResponses(JsonArray input)
    {
        var sb = new StringBuilder();
        foreach (var node in input)
        {
            if (node is not JsonObject obj)
                continue;

            var type = obj["type"]?.GetValue<string>();
            if (string.Equals(type, "function_call", StringComparison.Ordinal)
                || string.Equals(type, "function_call_output", StringComparison.Ordinal))
            {
                sb.Append(obj.ToJsonString());
                continue;
            }

            if (string.Equals(obj["role"]?.GetValue<string>(), "user", StringComparison.Ordinal)
                && obj["content"]?.GetValue<string>() is { } text
                && text.Contains(DysonInjectedUserComments.Marker, StringComparison.Ordinal))
                sb.Append(text);
        }

        return sb.ToString();
    }

    private static DysonToolCall Call(string id, string name) => new()
    {
        CallId = id,
        ToolName = name,
        Stage = 0,
        ArgumentsJson = "{}",
    };

    private static DysonToolCallResult Result(string id, string name, string content) => new()
    {
        CallId = id,
        ToolName = name,
        Stage = 0,
        Content = content,
    };

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

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
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
