using System.Net;
using System.Reflection;
using System.Text;

using DysonHarness;

using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// Live context compactor over the OpenAI-compatible Completions tool loop: live counter, both trip points,
/// the continuation guard, bus and null-bus trip paths, sub-agent exclusions, the post-compaction chip value,
/// the durable pending marker (restart, prompt-start read, failed summary), end-of-turn compaction, and the
/// untrimmed summary.
/// Limits are in <c>DysonDropContextTests</c> / <c>DysonMetaAgentContextCapTests</c>; the reasoning setting in
/// <c>OpenAiToolRoundReplayTests</c>.
/// </summary>
public class DysonContextCompactionSmokeTests
{
    [Fact]
    public async Task TripBeforeSend_NoBus_PausesSummarizesAndQueuesContinuation()
    {
        var workDir = CreateWorkDir();
        try
        {
            var handler = new ScriptedHandler([() => Sse(TextChunk("# Session summary: smoke\\n\\n## Goal\\nKeep going"))]);
            using var http = new HttpClient(handler);
            var session = CreateSession(http, workDir);
            session.MaxTargetContextTokens = 50;

            var result = await session.PromptAsync("# Task\n\ndo the thing");

            Assert.True(result.IsSuccess, result.IsError ? result.Error : "");
            Assert.Equal(1, handler.PostCount);
            Assert.Contains("Context compaction", handler.Bodies[0], StringComparison.Ordinal);
            Assert.Equal(2, session.Turns.Count);
            Assert.Equal("Paused for context compaction", session.Turns[0].AgentTitle);
            Assert.Equal(DysonAgentTurnKind.FullSummarize, session.Turns[1].Kind);
            Assert.Contains("Keep going", session.Turns[1].AssistantText, StringComparison.Ordinal);
            Assert.True(session.TryDequeuePendingTurn(out var next));
            Assert.True(DysonCompactionFlow.IsCompactionContinuation(next));
            Assert.Equal(0, session.LiveContextTokens);
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public async Task TripAfterResponse_Bus_RunsToolsThenCompactsOnce()
    {
        var workDir = CreateWorkDir();
        try
        {
            var handler = new ScriptedHandler(
            [
                () => Sse(
                    ToolCallChunk("call_1", "NoSuchSmokeTool", "{}"),
                    """{"id":"c1","choices":[],"usage":{"prompt_tokens":2000000,"completion_tokens":10}}"""),
                () => Sse(TextChunk("# Session summary: smoke\\n\\n## Next\\nResume")),
            ]);
            using var http = new HttpClient(handler);
            using var bus = new DysonMessageBus();
            var session = CreateSession(http, workDir, bus);
            var persistenceId = Guid.NewGuid();
            SetPersistenceId(session, persistenceId);
            session.MaxTargetContextTokens = 1_000_000;

            var limitEvents = new List<DysonSessionContextLimitReachedEvent>();
            var tokenEvents = new List<DysonSessionContextTokensChangedEvent>();
            using var limitSub = bus.Subscribe<DysonSessionContextLimitReachedEvent>(
                DysonBusScopes.Wildcard, limitEvents.Add).Value;
            using var tokenSub = bus.Subscribe<DysonSessionContextTokensChangedEvent>(
                DysonBusScopes.Wildcard, tokenEvents.Add).Value;

            var result = await session.PromptAsync("# Task\n\ndo the thing");

            Assert.True(result.IsSuccess, result.IsError ? result.Error : "");
            Assert.Equal(2, handler.PostCount);
            var interrupted = session.Turns[0];
            Assert.Single(interrupted.ToolCalls);
            Assert.Equal("Paused for context compaction", interrupted.AgentTitle);
            Assert.Equal(DysonAgentTurnKind.FullSummarize, session.Turns[^1].Kind);
            var trip = Assert.Single(limitEvents);
            Assert.Equal(persistenceId, trip.PersistenceId);
            Assert.Equal(interrupted.Id, trip.TurnId);
            Assert.Contains(tokenEvents, e => e.Tokens == 2_000_010);
            Assert.True(session.TryDequeuePendingTurn(out var next));
            Assert.True(DysonCompactionFlow.IsCompactionContinuation(next));
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public async Task SummaryOverModelWindow_FallsBackToTrimmedSummarizer()
    {
        var workDir = CreateWorkDir();
        try
        {
            var handler = new ScriptedHandler(
            [
                () => new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":{"code":"context_length_exceeded","message":"This model's maximum context length is 100 tokens."}}""",
                        Encoding.UTF8,
                        "application/json"),
                },
                () => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"id":"s1","choices":[{"index":0,"message":{"role":"assistant","content":"Fallback summary body"}}]}""",
                        Encoding.UTF8,
                        "application/json"),
                },
            ]);
            using var http = new HttpClient(handler);
            var session = CreateSession(http, workDir);
            session.MaxTargetContextTokens = 50;

            var result = await session.PromptAsync("# Task\n\ndo the thing");

            Assert.True(result.IsSuccess, result.IsError ? result.Error : "");
            Assert.Equal(2, handler.PostCount);
            Assert.Contains("do the thing", handler.Bodies[1], StringComparison.Ordinal);
            Assert.Contains("under about 35,000 tokens", handler.Bodies[1], StringComparison.Ordinal);
            var summary = session.Turns[^1];
            Assert.Equal(DysonAgentTurnKind.FullSummarize, summary.Kind);
            Assert.Contains("Fallback summary body", summary.AssistantText, StringComparison.Ordinal);
            Assert.True(session.TryDequeuePendingTurn(out _));
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public async Task LiveCounter_GrowsEveryHopWithInFlightRounds_AndUsesProviderUsage()
    {
        var workDir = CreateWorkDir();
        try
        {
            var handler = new ScriptedHandler(
            [
                () => Sse(ToolCallChunk("call_1", "NoSuchSmokeTool", "{}")),
                () => Sse(ToolCallChunk("call_2", "NoSuchSmokeTool", "{}")),
                () => Sse(
                    TextChunk("done"),
                    """{"id":"c1","choices":[],"usage":{"prompt_tokens":900,"completion_tokens":7}}"""),
            ]);
            using var http = new HttpClient(handler);
            using var bus = new DysonMessageBus();
            var session = CreateSession(http, workDir, bus);
            SetPersistenceId(session, Guid.NewGuid());
            session.MaxTargetContextTokens = 0;
            var tokens = new List<int>();
            using var sub = bus.Subscribe<DysonSessionContextTokensChangedEvent>(
                DysonBusScopes.Wildcard, e => tokens.Add(e.Tokens)).Value;

            var result = await session.PromptAsync("# Task\n\ndo the thing");

            Assert.True(result.IsSuccess, result.IsError ? result.Error : "");
            Assert.Equal(3, handler.PostCount);
            Assert.Contains("call_1", handler.Bodies[2], StringComparison.Ordinal);
            Assert.Contains("call_2", handler.Bodies[2], StringComparison.Ordinal);
            // Three requests and three responses; every hop before the usage-backed reply grows.
            Assert.Equal(6, tokens.Count);
            for (var i = 1; i < tokens.Count - 1; i++)
                Assert.True(tokens[i] > tokens[i - 1], $"hop {i}: {tokens[i]} <= {tokens[i - 1]}");
            Assert.Equal(907, tokens[^1]);
            Assert.Single(session.Turns);
            Assert.False(session.TryDequeuePendingTurn(out _));
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public async Task TripBeforeSend_HostApplyExcludesEarlierTurnsAndPublishesPostCompactionSize()
    {
        var workDir = CreateWorkDir();
        try
        {
            var handler = new ScriptedHandler(
            [
                () => Sse(TextChunk("first done")),
                () => Sse(TextChunk("# Session summary: smoke\\n\\n## Next\\nResume")),
            ]);
            using var http = new HttpClient(handler);
            using var bus = new DysonMessageBus();
            var session = CreateSession(http, workDir, bus);
            SetPersistenceId(session, Guid.NewGuid());
            session.MaxTargetContextTokens = 0;
            Assert.True((await session.PromptAsync("# Task\n\nfirst")).IsSuccess);

            session.MaxTargetContextTokens = 50;
            var result = await session.PromptAsync("# Task\n\nsecond");

            Assert.True(result.IsSuccess, result.IsError ? result.Error : "");
            Assert.Equal(2, handler.PostCount);
            Assert.Equal(3, session.Turns.Count);
            var summary = session.Turns[^1];
            Assert.Equal(DysonAgentTurnKind.FullSummarize, summary.Kind);
            Assert.True(session.TryDequeuePendingTurn(out var next));
            Assert.True(DysonCompactionFlow.IsCompactionContinuation(next));

            var before = session.EstimateOutgoingContextTokens();
            var published = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = bus.Subscribe<DysonSessionContextTokensChangedEvent>(
                DysonBusScopes.Wildcard, e => published.TrySetResult(e.Tokens)).Value;

            // What the root host does after the prompt returns with FullSummarize last.
            var dropped = DysonFullSummarizeFlow.ApplyAfterCompletion(session, summary);

            Assert.Equal(new[] { session.Turns[0].Id, session.Turns[1].Id }, dropped.Select(t => t.Id));
            Assert.False(summary.IsExcludedFromContext);
            var after = await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(session.EstimateOutgoingContextTokens(), after);
            Assert.True(after < before, $"post-compaction {after} must be under pre-compaction {before}");
            Assert.Equal(after, session.LiveContextTokens);
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public async Task CompactionContinuation_SendsFirstRequestOverLimit_TripsAfterFirstToolRound()
    {
        var workDir = CreateWorkDir();
        try
        {
            var handler = new ScriptedHandler(
            [
                () => Sse(ToolCallChunk("call_1", "NoSuchSmokeTool", "{}")),
                () => Sse(TextChunk("# Session summary: smoke\\n\\n## Next\\nResume")),
            ]);
            using var http = new HttpClient(handler);
            var session = CreateSession(http, workDir);
            session.MaxTargetContextTokens = 50;

            var result = await session.PromptHarnessTurnAsync(DysonCompactionFlow.CreateContinuationTurn());

            Assert.True(result.IsSuccess, result.IsError ? result.Error : "");
            Assert.Equal(2, handler.PostCount);
            Assert.Contains("Resume the interrupted task", handler.Bodies[0], StringComparison.Ordinal);
            Assert.Contains("Context compaction", handler.Bodies[1], StringComparison.Ordinal);
            var continuation = session.Turns[0];
            Assert.Single(continuation.ToolCalls);
            Assert.Equal("Paused for context compaction", continuation.AgentTitle);
            Assert.Equal(DysonAgentTurnKind.FullSummarize, session.Turns[1].Kind);
            Assert.True(session.TryDequeuePendingTurn(out var next));
            Assert.True(DysonCompactionFlow.IsCompactionContinuation(next));
            Assert.Single(session.SnapshotLog(), l => l.Contains("over the limit before its first tool round", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public void CanCompact_ExcludesExploreAndSummary_ContinuationOnlyAfterAToolRound()
    {
        using var http = new HttpClient();
        var work = CreateSession(http, Path.GetTempPath());
        var explore = CreateSession(http, Path.GetTempPath(), mode: DysonAgentModes.Explore);
        var normal = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal, StartedUtc = DateTime.UtcNow };
        var continuation = DysonCompactionFlow.CreateContinuationTurn();

        Assert.True(DysonCompactionFlow.CanCompact(work, normal));
        Assert.False(DysonCompactionFlow.CanCompact(explore, normal));
        Assert.False(DysonCompactionFlow.CanCompact(work, DysonCompactionFlow.CreateSummaryTurn()));
        Assert.False(DysonCompactionFlow.CanCompact(work, continuation));
        continuation.ToolRoundCallCounts.Add(1);
        Assert.True(DysonCompactionFlow.CanCompact(work, continuation));
        Assert.False(DysonCompactionFlow.IsCompactionContinuation(
            DysonAgentSession.CreateNormalTurn(DysonCompactionFlow.ContinuationInstruction)));
    }

    [Fact]
    public void ReportLiveContextTokens_BusHandlerTripsOncePerTurn_NullBusSetsFlag_OffNever()
    {
        using var http = new HttpClient();
        using var bus = new DysonMessageBus();
        var session = CreateSession(http, Path.GetTempPath(), bus);
        SetPersistenceId(session, Guid.NewGuid());
        session.MaxTargetContextTokens = 100;
        var trips = new List<DysonSessionContextLimitReachedEvent>();
        using var sub = bus.Subscribe<DysonSessionContextLimitReachedEvent>(DysonBusScopes.Wildcard, trips.Add).Value;
        using var watch = session.WatchContextLimit();
        Assert.NotNull(watch);
        var turn = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal, StartedUtc = DateTime.UtcNow };

        session.ReportLiveContextTokens(turn, 100);
        Assert.False(session.IsCompactionRequested(turn));
        session.ReportLiveContextTokens(turn, 150);
        Assert.True(session.IsCompactionRequested(turn));
        session.ReportLiveContextTokens(turn, 400);
        Assert.Equal(turn.Id, Assert.Single(trips).TurnId);
        Assert.Equal(150, trips[0].Tokens);
        Assert.Equal(100, trips[0].Limit);

        var off = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal, StartedUtc = DateTime.UtcNow };
        session.MaxTargetContextTokens = 0;
        session.ReportLiveContextTokens(off, 10_000_000);
        Assert.False(session.IsCompactionRequested(off));
        Assert.Single(trips);

        var noBus = CreateSession(http, Path.GetTempPath());
        noBus.MaxTargetContextTokens = 100;
        Assert.Null(noBus.WatchContextLimit());
        var direct = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal, StartedUtc = DateTime.UtcNow };
        noBus.ReportLiveContextTokens(direct, 150);
        Assert.True(noBus.IsCompactionRequested(direct));
    }

    [Theory]
    [InlineData(DysonAgentModes.Work, 0)]
    [InlineData(DysonAgentModes.Explore, 50)]
    public async Task LimitOffOrExplore_NeverCompacts(string mode, int limit)
    {
        var workDir = CreateWorkDir();
        try
        {
            var handler = new ScriptedHandler([() => Sse(TextChunk("done"))]);
            using var http = new HttpClient(handler);
            var session = CreateSession(http, workDir, mode: mode);
            session.MaxTargetContextTokens = limit;

            var result = await session.PromptAsync("# Task\n\ndo the thing");

            Assert.True(result.IsSuccess, result.IsError ? result.Error : "");
            Assert.Equal(1, handler.PostCount);
            var turn = Assert.Single(session.Turns);
            Assert.NotEqual("Paused for context compaction", turn.AgentTitle);
            Assert.False(session.TryDequeuePendingTurn(out _));
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public async Task SubagentCompaction_ExcludesEarlierTurnsAndRaisesTurnUpdatedForPersistence()
    {
        var workDir = CreateWorkDir();
        try
        {
            var handler = new ScriptedHandler(
                [() => Sse(TextChunk("# Session summary: child\\n\\n## Next\\nResume"))]);
            using var http = new HttpClient(handler);
            var parent = CreateSession(http, workDir);
            var child = CreateSession(http, workDir);
            typeof(DysonAgentSession).GetProperty(nameof(DysonAgentSession.Id))!.SetValue(child, 1);
            parent.RestoreRegisteredSubagent(child);
            Assert.Same(parent, child.Parent);
            child.MaxTargetContextTokens = 50;
            var updated = new List<DysonAgentTurn>();
            child.TurnUpdated += (_, t) => updated.Add(t);

            var result = await child.PromptAsync("# Task\n\nchild work");

            Assert.True(result.IsSuccess, result.IsError ? result.Error : "");
            var interrupted = child.Turns[0];
            var summary = child.Turns[^1];
            Assert.Equal(DysonAgentTurnKind.FullSummarize, summary.Kind);
            Assert.True(interrupted.IsExcludedFromContext);
            Assert.False(summary.IsExcludedFromContext);
            Assert.Equal(new[] { interrupted.Id, summary.Id }, updated.Select(t => t.Id));
            Assert.True(DysonTurnPersistence.ToEntity(interrupted, Guid.NewGuid(), 0).IsExcludedFromContext);
            Assert.True(child.TryDequeuePendingTurn(out var next));
            Assert.True(DysonCompactionFlow.IsCompactionContinuation(next));
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public void ReportLiveContextTokens_SetsFlagWithoutSubscriberAndWithThrowingHandler()
    {
        using var http = new HttpClient();
        using var bus = new DysonMessageBus();

        var unsubscribed = CreateSession(http, Path.GetTempPath(), bus);
        SetPersistenceId(unsubscribed, Guid.NewGuid());
        unsubscribed.MaxTargetContextTokens = 100;
        var first = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal, StartedUtc = DateTime.UtcNow };
        unsubscribed.ReportLiveContextTokens(first, 150);
        Assert.True(unsubscribed.IsCompactionRequested(first));

        var throwing = CreateSession(http, Path.GetTempPath(), bus);
        var persistenceId = Guid.NewGuid();
        SetPersistenceId(throwing, persistenceId);
        throwing.MaxTargetContextTokens = 100;
        using var sub = bus.Subscribe<DysonSessionContextLimitReachedEvent>(
            DysonBusScopes.Session(persistenceId),
            _ => throw new InvalidOperationException("handler boom")).Value;
        var second = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal, StartedUtc = DateTime.UtcNow };
        throwing.ReportLiveContextTokens(second, 150);
        Assert.True(throwing.IsCompactionRequested(second));
    }

    [Fact]
    public async Task Trip_PersistsMarker_RestartLoadsIt_AndCompactsBeforeFirstSend()
    {
        var workDir = CreateWorkDir();
        var (accessor, dbPath) = DysonTempDb.OpenFileAccessor();
        try
        {
            var store = DysonTempDb.Sessions(accessor);
            var sessionId = await CreateSessionRowAsync(store);
            using (var deadHttp = new HttpClient(new ScriptedHandler([])))
            {
                // Process 1 trips mid-turn, then dies before it can pause.
                var before = await LoadAsync(store, sessionId, deadHttp, workDir);
                before.MaxTargetContextTokens = 100;
                var tripped = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal, StartedUtc = DateTime.UtcNow };
                before.ReportLiveContextTokens(tripped, 150);
                await before.CompactionMarkerWrites;
                Assert.Equal(tripped.Id, (await store.GetCompactionPendingTurnIdAsync(sessionId)).Value);
            }

            var handler = new ScriptedHandler(
            [
                () => Sse(TextChunk("# Session summary: restart\\n\\n## Next\\nAnswer the prompt")),
                () => Sse(TextChunk("answered")),
            ]);
            using var http = new HttpClient(handler);
            var after = await LoadAsync(store, sessionId, http, workDir);
            Assert.True(after.IsCompactionPending);
            after.MaxTargetContextTokens = 0;

            var result = await after.PromptAsync("# Task\n\nnext prompt");

            Assert.True(result.IsSuccess, result.IsError ? result.Error : "");
            Assert.Equal(2, handler.PostCount);
            Assert.Contains("Context compaction", handler.Bodies[0], StringComparison.Ordinal);
            Assert.DoesNotContain("next prompt", handler.Bodies[0], StringComparison.Ordinal);
            Assert.Contains("next prompt", handler.Bodies[1], StringComparison.Ordinal);
            Assert.Equal(DysonAgentTurnKind.FullSummarize, after.Turns[0].Kind);
            Assert.Equal("answered", after.Turns[1].AssistantText);
            Assert.False(after.TryDequeuePendingTurn(out _));
            Assert.False(after.IsCompactionPending);
            await after.CompactionMarkerWrites;
            Assert.Null((await store.GetCompactionPendingTurnIdAsync(sessionId)).Value);
        }
        finally
        {
            TryDelete(workDir);
            TryDeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task PromptStart_ReadsDbMarkerNotInMemory_AndCompactsFirst()
    {
        var workDir = CreateWorkDir();
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        try
        {
            var store = DysonTempDb.Sessions(accessor);
            var sessionId = await CreateSessionRowAsync(store);
            var handler = new ScriptedHandler(
            [
                () => Sse(TextChunk("# Session summary: db\\n\\n## Next\\nGo")),
                () => Sse(TextChunk("answered")),
            ]);
            using var http = new HttpClient(handler);
            var session = await LoadAsync(store, sessionId, http, workDir);
            session.MaxTargetContextTokens = 0;
            Assert.False(session.IsCompactionPending);
            var written = await store.UpdateSessionMetaAsync(new DysonSessionMetaUpdate
            {
                SessionId = sessionId,
                UpdateCompactionPending = true,
                CompactionPendingTurnId = Guid.NewGuid(),
            });
            Assert.True(written.IsSuccess);

            var result = await session.PromptAsync("# Task\n\nnext prompt");

            Assert.True(result.IsSuccess, result.IsError ? result.Error : "");
            Assert.Equal(2, handler.PostCount);
            Assert.Contains("Context compaction", handler.Bodies[0], StringComparison.Ordinal);
            Assert.Equal(DysonAgentTurnKind.FullSummarize, session.Turns[0].Kind);
            await session.CompactionMarkerWrites;
            Assert.Null((await store.GetCompactionPendingTurnIdAsync(sessionId)).Value);
        }
        finally
        {
            await connection.DisposeAsync();
            TryDelete(workDir);
        }
    }

    [Fact]
    public async Task FinalReplyOverLimit_CompactsAtTurnEnd_WithoutContinuation()
    {
        var workDir = CreateWorkDir();
        try
        {
            var handler = new ScriptedHandler(
            [
                () => Sse(
                    TextChunk("# Done\\n\\nAll finished"),
                    """{"id":"c1","choices":[],"usage":{"prompt_tokens":2000000,"completion_tokens":10}}"""),
                () => Sse(TextChunk("# Session summary: done\\n\\n## Status\\nFinished")),
            ]);
            using var http = new HttpClient(handler);
            var session = CreateSession(http, workDir);
            session.MaxTargetContextTokens = 1_000_000;

            var result = await session.PromptAsync("# Task\n\ndo the thing");

            Assert.True(result.IsSuccess, result.IsError ? result.Error : "");
            Assert.Equal(2, handler.PostCount);
            Assert.Equal(2, session.Turns.Count);
            Assert.Equal("Done", session.Turns[0].AgentTitle);
            Assert.Equal(DysonAgentTurnKind.FullSummarize, session.Turns[1].Kind);
            Assert.False(session.TryDequeuePendingTurn(out _));
            Assert.False(session.IsCompactionPending);
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public async Task EndsCurrentTurnOverLimit_CompactsWithoutContinuation_QueuedTurnStillRuns()
    {
        var workDir = CreateWorkDir();
        try
        {
            var handler = new ScriptedHandler(
            [
                () => Sse(
                    ToolCallChunk("call_1", "StartNewTurn", """{\"promptInstructions\":\"next step\"}"""),
                    """{"id":"c1","choices":[],"usage":{"prompt_tokens":2000000,"completion_tokens":10}}"""),
                () => Sse(TextChunk("# Session summary: ended\\n\\n## Next\\nnext step")),
            ]);
            using var http = new HttpClient(handler);
            var session = CreateSession(http, workDir);
            session.MaxTargetContextTokens = 1_000_000;

            var result = await session.PromptAsync("# Task\n\ndo the thing");

            Assert.True(result.IsSuccess, result.IsError ? result.Error : "");
            Assert.Equal(2, handler.PostCount);
            Assert.NotEqual("Paused for context compaction", session.Turns[0].AgentTitle);
            Assert.Equal(DysonAgentTurnKind.FullSummarize, session.Turns[^1].Kind);
            Assert.True(session.TryDequeuePendingTurn(out var next));
            Assert.Equal("next step", next.Instruction);
            Assert.False(DysonCompactionFlow.IsCompactionContinuation(next));
            Assert.False(session.TryDequeuePendingTurn(out _));
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public async Task LongSummary_IsNotTrimmed_AndInstructionCarriesTheGuideline()
    {
        var workDir = CreateWorkDir();
        try
        {
            var body = new string('x', 10_000);
            var handler = new ScriptedHandler([() => Sse(TextChunk("# Session summary: long\\n\\n" + body))]);
            using var http = new HttpClient(handler);
            var session = CreateSession(http, workDir);
            session.MaxTargetContextTokens = 50;

            var result = await session.PromptAsync("# Task\n\ndo the thing");

            Assert.True(result.IsSuccess, result.IsError ? result.Error : "");
            Assert.Contains("under about 35,000 tokens (roughly 140,000 characters)", handler.Bodies[0], StringComparison.Ordinal);
            Assert.DoesNotContain("6,000", handler.Bodies[0], StringComparison.Ordinal);
            var summary = session.Turns[^1];
            DysonFullSummarizeFlow.ApplyAfterCompletion(session, summary);
            Assert.Equal(10_000, summary.AssistantText!.Count(c => c == 'x'));
            Assert.Contains("35,000 tokens", DysonTurnSummarizerPrompt.CompactionSystem, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public async Task FailedSummary_KeepsMarker_SurfacesError_NextPromptRetries()
    {
        var workDir = CreateWorkDir();
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        try
        {
            var store = DysonTempDb.Sessions(accessor);
            var sessionId = await CreateSessionRowAsync(store);
            var handler = new ScriptedHandler(
            [
                () => new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":{"code":"invalid_request","message":"summary rejected"}}""",
                        Encoding.UTF8,
                        "application/json"),
                },
                () => Sse(TextChunk("# Session summary: retry\\n\\n## Next\\nGo")),
                () => Sse(TextChunk("answered")),
            ]);
            using var http = new HttpClient(handler);
            var session = await LoadAsync(store, sessionId, http, workDir);
            session.MaxTargetContextTokens = 50;

            var failed = await session.PromptAsync("# Task\n\ndo the thing");

            Assert.True(failed.IsError);
            Assert.Contains(DysonCompactionFlow.FailedTitle, failed.Error, StringComparison.Ordinal);
            Assert.Equal("Paused for context compaction", session.Turns[0].AgentTitle);
            var failedSummary = session.Turns[1];
            Assert.Equal(DysonCompactionFlow.FailedTitle, failedSummary.AgentTitle);
            Assert.True(failedSummary.IsExcludedFromContext);
            Assert.False(session.TryDequeuePendingTurn(out _));
            Assert.True(session.IsCompactionPending);
            await session.CompactionMarkerWrites;
            Assert.Equal(session.Turns[0].Id, (await store.GetCompactionPendingTurnIdAsync(sessionId)).Value);

            session.MaxTargetContextTokens = 0;
            var retried = await session.PromptAsync("# Task\n\nretry");

            Assert.True(retried.IsSuccess, retried.IsError ? retried.Error : "");
            Assert.Equal(3, handler.PostCount);
            Assert.Contains("Context compaction", handler.Bodies[1], StringComparison.Ordinal);
            Assert.DoesNotContain("summary request failed", handler.Bodies[1], StringComparison.Ordinal);
            Assert.Equal(DysonAgentTurnKind.FullSummarize, session.Turns[2].Kind);
            Assert.False(session.Turns[2].IsExcludedFromContext);
            Assert.Equal("answered", session.Turns[3].AssistantText);
            await session.CompactionMarkerWrites;
            Assert.Null((await store.GetCompactionPendingTurnIdAsync(sessionId)).Value);
        }
        finally
        {
            await connection.DisposeAsync();
            TryDelete(workDir);
        }
    }

    [Fact]
    public void ContextLengthClassifier()
    {
        Assert.True(OpenAiCompatibleHttp.IsContextLengthError("OpenAI API 400 Bad Request: {\"code\":\"context_length_exceeded\"}"));
        Assert.True(OpenAiCompatibleHttp.IsContextLengthError("OpenAI API 400: prompt is too long: 210000 tokens > 200000 maximum"));
        Assert.False(OpenAiCompatibleHttp.IsContextLengthError("OpenAI API 503 Service Unavailable: upstream"));
        Assert.False(OpenAiCompatibleHttp.IsContextLengthError(null));
    }

    private static void SetPersistenceId(DysonAgentSession session, Guid persistenceId) =>
        typeof(DysonAgentSession)
            .GetMethod("SetPersistenceId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [persistenceId]);

    private static OpenAiCompatibleAgentSession CreateSession(
        HttpClient http,
        string workDir,
        DysonMessageBus? bus = null,
        string mode = DysonAgentModes.Work) =>
        new(
            mode,
            new DysonAgentSessionConfig { Bus = bus },
            CreateProvider(),
            http,
            workDir);

    private static OpenAiCompatibleAgentProvider CreateProvider()
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

    private static async Task<Guid> CreateSessionRowAsync(IDysonSessionRepository store)
    {
        var created = await store.CreateSessionAsync(new DysonSessionCreateRequest
        {
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "",
        });
        Assert.True(created.IsSuccess, created.IsError ? created.Error : "");
        return created.Value;
    }

    /// <summary>Session activate: hydrate from the DB row (same path as resume / restart).</summary>
    private static async Task<OpenAiCompatibleAgentSession> LoadAsync(
        IDysonSessionRepository store,
        Guid sessionId,
        HttpClient http,
        string workDir)
    {
        var loaded = await OpenAiCompatibleAgentSession.LoadAsync(
            store,
            sessionId,
            CreateProvider(),
            http,
            workDir,
            new DysonAgentSessionConfig(),
            appendResumeLog: false);
        Assert.True(loaded.IsSuccess, loaded.IsError ? loaded.Error : "");
        return loaded.Value;
    }

    private static void TryDeleteDb(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // best-effort temp cleanup
            }
        }
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
        var dir = Path.Combine(Path.GetTempPath(), "dyson-compaction-" + Guid.NewGuid().ToString("N"));
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
