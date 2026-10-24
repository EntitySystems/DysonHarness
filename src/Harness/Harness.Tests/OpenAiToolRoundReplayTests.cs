using System.Text;
using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Completed turns with recorded round counts replay the same tool blocks as the in-flight
/// rebuild. Responses reasoning from a finished turn replays only when preserveReasoning is on
/// and the stored source matches. Legacy turns stay one chunk.
/// </summary>
public class OpenAiToolRoundReplayTests
{
    private const string ReasoningMarker = "SECRET_REASONING_BLOB";
    private const string FinalReasoningMarker = "FINAL_REASONING_BLOB";

    [Fact]
    public void Run()
    {
        AssertRecordedRoundsMatchInFlight();
        AssertLegacyTurnStaysOneCompletionsChunk();
        AssertRoundCountsRoundTrip();
    }

    [Fact]
    public void FinishedTurnReasoningReplaysOnlyWhenOnAndSourceMatches()
    {
        var session = new StubSession();
        var turn = FinishedTurnWithReasoning(source: null);
        session.AddTurnForTest(turn);

        var on = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, null, null, preserveReasoning: true).Input;
        var types = on.OfType<JsonObject>().Select(o => o["type"]?.GetValue<string>() ?? o["role"]?.GetValue<string>()).ToList();
        Assert.Equal(
            new string?[] { "user", "reasoning", "function_call", "function_call_output", "reasoning", "assistant" },
            types);
        Assert.Contains(ReasoningMarker, on[1]!.ToJsonString());
        Assert.Contains(FinalReasoningMarker, on[4]!.ToJsonString());

        var off = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, null, null, preserveReasoning: false).Input.ToJsonString();
        Assert.DoesNotContain(ReasoningMarker, off);
        Assert.DoesNotContain(FinalReasoningMarker, off);

        // Completions never replays reasoning items.
        var completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(session, null, null)
            .Messages.ToJsonString();
        Assert.DoesNotContain(ReasoningMarker, completions);
        Assert.DoesNotContain(FinalReasoningMarker, completions);

        turn.ReasoningItemsSource = DysonManagedSources.CliProxyCodex;
        var foreign = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, null, null, preserveReasoning: true).Input.ToJsonString();
        Assert.DoesNotContain(ReasoningMarker, foreign);
        Assert.DoesNotContain(FinalReasoningMarker, foreign);
    }

    [Fact]
    public void UnmanagedProvidersWithDifferentBaseUrlsDoNotShareReasoning()
    {
        var local = UnmanagedProvider("http://localhost:1234/v1");
        var other = UnmanagedProvider("https://api.example.com/v1");
        Assert.Equal("http://localhost:1234", local.ReasoningSourceTag);
        Assert.Equal("https://api.example.com", other.ReasoningSourceTag);

        var session = new StubSession { Provider = local };
        session.AddTurnForTest(FinishedTurnWithReasoning(local.ReasoningSourceTag));
        Assert.Contains(ReasoningMarker, BuildPreserving(session));

        session.Provider = other;
        Assert.DoesNotContain(ReasoningMarker, BuildPreserving(session));

        // Same host, different path: same server, blobs stay valid.
        session.Provider = UnmanagedProvider("http://LOCALHOST:1234/v1/");
        Assert.Contains(ReasoningMarker, BuildPreserving(session));

        // Older rows stored a null source; they only match a null current tag.
        var legacy = new StubSession { Provider = local };
        legacy.AddTurnForTest(FinishedTurnWithReasoning(source: null));
        Assert.DoesNotContain(ReasoningMarker, BuildPreserving(legacy));
        legacy.Provider = new StubProvider();
        Assert.Contains(ReasoningMarker, BuildPreserving(legacy));
    }

    private static string BuildPreserving(DysonAgentSession session) =>
        OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, null, null, preserveReasoning: true).Input.ToJsonString();

    private static OpenAiCompatibleAgentProvider UnmanagedProvider(string baseUrl)
    {
        var entity = new DysonModelProviderEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Custom",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = baseUrl,
            OpenAiApiMode = DysonOpenAiApiModes.Responses,
        };
        return new OpenAiCompatibleAgentProvider(entity, new DysonModelSlugEntity
        {
            Id = Guid.NewGuid(),
            ProviderId = entity.Id,
            Slug = "model",
            DisplayAlias = "model",
            Provider = entity,
        });
    }

    [Fact]
    public async Task PreserveSettingIsReadPerTurnAndMetaAgentIsAlwaysOn()
    {
        var off = new FakeSettings { Values = { [DysonAppSettingKeys.PreserveReasoningTraces] = "false" } };

        var work = new StubSession(DysonAgentModes.Work, new DysonAgentSessionConfig { AppSettings = off });
        work.AddTurnForTest(FinishedTurnWithReasoning(source: null));
        Assert.False(await work.RefreshPreserveReasoningTracesAsync(CancellationToken.None));
        Assert.DoesNotContain(ReasoningMarker, BuildWithSessionSetting(work));

        var meta = new StubSession(DysonAgentModes.MetaAgent, new DysonAgentSessionConfig { AppSettings = off });
        meta.AddTurnForTest(FinishedTurnWithReasoning(source: null));
        var readsBefore = off.Reads;
        Assert.True(await meta.RefreshPreserveReasoningTracesAsync(CancellationToken.None));
        Assert.Equal(readsBefore, off.Reads);
        Assert.Contains(ReasoningMarker, BuildWithSessionSetting(meta));

        Assert.True(await new StubSession(DysonAgentModes.Work, new DysonAgentSessionConfig { AppSettings = new FakeSettings() })
            .RefreshPreserveReasoningTracesAsync(CancellationToken.None));
        Assert.True(await new StubSession(DysonAgentModes.Work, new DysonAgentSessionConfig { AppSettings = new FakeSettings { Fail = true } })
            .RefreshPreserveReasoningTracesAsync(CancellationToken.None));

        off.Values.Remove(DysonAppSettingKeys.PreserveReasoningTraces);
        Assert.True(await work.RefreshPreserveReasoningTracesAsync(CancellationToken.None));
        Assert.Contains(ReasoningMarker, BuildWithSessionSetting(work));
    }

    private static string BuildWithSessionSetting(DysonAgentSession session) =>
        OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, null, null, preserveReasoning: session.PreserveReasoningTraces).Input.ToJsonString();

    private static DysonAgentTurn FinishedTurnWithReasoning(string? source)
    {
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "do the tool",
            StartedUtc = DateTime.UtcNow,
        };
        turn.ToolCalls.Add(Call("c1", "Grep"));
        turn.ResponseLog.Enqueue(Result("c1", "Grep", "one"));
        turn.ToolRoundCallCounts.Add(1);
        turn.RecordReasoningItems([Reasoning("rs_1", ReasoningMarker)], source, finalRound: false);
        turn.RecordReasoningItems([Reasoning("rs_2", FinalReasoningMarker)], source, finalRound: true);
        turn.AssistantText = "done";
        turn.CompletedUtc = DateTime.UtcNow;
        return turn;
    }

    private static JsonObject Reasoning(string id, string blob) => new()
    {
        ["type"] = "reasoning",
        ["id"] = id,
        ["encrypted_content"] = blob,
    };

    private static void AssertRecordedRoundsMatchInFlight()
    {
        const string reasoningMarker = ReasoningMarker;
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
        var reasoning = Reasoning("rs_test", reasoningMarker);
        turn.ToolRoundCallCounts.Add(2);
        turn.RecordReasoningItems([reasoning], sourceTag: null, finalRound: false);
        turn.ToolRoundCallCounts.Add(1);
        turn.RecordReasoningItems([], sourceTag: null, finalRound: false);
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
            session, currentUserPrompt: null, currentFilePaths: null, preserveReasoning: true);
        var doneResponsesOff = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, currentUserPrompt: null, currentFilePaths: null, preserveReasoning: false);

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

        if (!liveResponses.Contains(reasoningMarker, StringComparison.Ordinal))
            throw new InvalidOperationException("In-flight Responses must carry the reasoning item.");

        if (doneResponsesOff.Input.ToJsonString().Contains(reasoningMarker, StringComparison.Ordinal))
            throw new InvalidOperationException("preserveReasoning off must omit finished-turn reasoning.");

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
        Assert.Empty(legacy.ToolRoundReasoningItems);
        Assert.Null(legacy.FinalReasoningItems);
        Assert.Null(legacy.ReasoningItemsSource);

        // No reasoning stored ⇒ no reasoning keys in the row.
        Assert.DoesNotContain("reasoning", json, StringComparison.OrdinalIgnoreCase);

        var withReasoning = FinishedTurnWithReasoning(DysonManagedSources.CliProxyCodex);
        var reasoningJson = DysonTurnToolStateSerializer.CaptureFromTurn(withReasoning);
        var restoredReasoning = new DysonAgentTurn();
        DysonTurnToolStateSerializer.ApplyToTurn(restoredReasoning, reasoningJson);
        Assert.Equal(DysonManagedSources.CliProxyCodex, restoredReasoning.ReasoningItemsSource);
        var round = Assert.Single(restoredReasoning.ToolRoundReasoningItems);
        Assert.Equal(ReasoningMarker, Assert.Single(round!)["encrypted_content"]?.GetValue<string>());
        Assert.Equal(FinalReasoningMarker, Assert.Single(restoredReasoning.FinalReasoningItems!)["encrypted_content"]?.GetValue<string>());
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
            if (type is "reasoning" or "function_call" or "function_call_output")
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

    private sealed class FakeSettings : IDysonSubjectSettingsRepository
    {
        public Dictionary<string, string> Values { get; } = [];
        public bool Fail { get; init; }
        public int Reads { get; private set; }

        public Task<VoidResult<string>> EnsureSubjectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<Result<string?, string>> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (Fail)
                return Task.FromResult(Result<string?, string>.AsError("db down"));
            Values.TryGetValue(key, out var value);
            return Task.FromResult(Result<string?, string>.AsValue(value));
        }

        public Task<VoidResult<string>> SetSettingAsync(string key, string? value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubSession(string mode = DysonAgentModes.Work, DysonAgentSessionConfig? config = null)
        : DysonAgentSession(mode, config ?? new DysonAgentSessionConfig(), new StubProvider())
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
