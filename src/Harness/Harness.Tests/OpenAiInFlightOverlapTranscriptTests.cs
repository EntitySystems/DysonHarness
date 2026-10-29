using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Two prompt loops in flight on one session (an inject turn plus a report turn) must not emit
/// the older loop's recorded tool rounds in history and again as its in-flight rounds.
/// </summary>
public class OpenAiInFlightOverlapTranscriptTests
{
    [Fact]
    public void Every_in_flight_turn_is_incomplete_so_tool_ids_appear_once()
    {
        var older = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "inject: keep going",
            StartedUtc = DateTime.UtcNow,
        };
        var t1 = Call("t1", "Grep");
        var t2 = Call("t2", "ReadFile");
        older.ToolCalls.Add(t1);
        older.ToolCalls.Add(t2);
        older.ToolRoundCallCounts.Add(2);
        var r1 = Result("t1", "Grep", "hits");
        var r2 = Result("t2", "ReadFile", "body");
        older.ResponseLog.Enqueue(r1);
        older.ResponseLog.Enqueue(r2);

        var newer = DysonSubagentReportPrompt.CreateTurn("# Subagent report\n\nchild done");

        var session = new StubSession();
        session.AddTurnForTest(older);
        session.AddTurnForTest(newer);

        JsonArray messages;
        string responses;
        using (session.BeginInFlightPrompt(older))
        using (session.BeginInFlightPrompt(newer))
        {
            var rounds = new[] { new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([t1, t2], [r1, r2]) };
            messages = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(session, null, null, rounds).Messages;
            responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                session, null, null, preserveReasoning: false, inFlightRounds: rounds).Input.ToJsonString();
        }

        var callIds = new List<string>();
        var resultIds = new List<string>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject obj)
                continue;

            var role = obj["role"]?.GetValue<string>();
            if (role == "assistant" && obj["tool_calls"] is JsonArray calls)
            {
                foreach (var call in calls)
                    callIds.Add(call!["id"]!.GetValue<string>());

                // Results follow their tool_calls directly: no user / report message in between.
                for (var j = 1; j <= calls.Count; j++)
                    Assert.Equal("tool", messages[i + j]?["role"]?.GetValue<string>());
            }
            else if (role == "tool")
            {
                resultIds.Add(obj["tool_call_id"]!.GetValue<string>());
            }
        }

        Assert.Equal(["t1", "t2"], callIds);
        Assert.Equal(["t1", "t2"], resultIds);
        // One function_call + one function_call_output each.
        Assert.Equal(2, Occurrences(responses, "\"call_id\":\"t1\""));
        Assert.Equal(2, Occurrences(responses, "\"call_id\":\"t2\""));
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
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
