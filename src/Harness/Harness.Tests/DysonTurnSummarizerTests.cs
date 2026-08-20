using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: SummarizeTurns worker caps, claims/dedupe/enqueue flag, MCP skip/reason, transcript stubs, SQLite existing-row ContextSummary upsert (Xunit Fact).
/// </summary>
public class DysonTurnSummarizerTests
{
    [Fact]
    public async Task Run()
    {
        AssertCapsAndHelpers();
        AssertSummarizeClaimsAndGate();
        AssertPersistenceRoundTrip();
        await AssertContextSummaryPersistsOnExistingRowUpsertAsync();
        AssertTranscriptEmitsSummaryStub();
        AssertDropContextPrefersSummarize();
        await AssertSummarizeTurnsToolAsync();
        await AssertSummarizeTurnsSkipsHasSummaryAndClaimAsync();
    }

    private static void AssertCapsAndHelpers()
    {
        if (DysonTurnSummarizer.MaxSummaryTokens != 2_000)
            throw new InvalidOperationException("MaxSummaryTokens must be 2000.");

        var tokens = new DysonTiktokenTokenCounter();
        var dense = string.Concat(Enumerable.Repeat("alpha beta gamma delta ", 400));
        var trimmed = DysonWebSearchSummarizer.TrimToMaxTokens(dense, tokens, DysonTurnSummarizer.MaxSummaryTokens);
        if (tokens.CountTokens(trimmed) > DysonTurnSummarizer.MaxSummaryTokens)
            throw new InvalidOperationException("TrimToMaxTokens must enforce 2K summarizer cap.");

        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "inspect auth",
            AssistantText = "found JwtBearer",
            CompactToolHistory = "ReadFile Program.cs → ok",
            ToolHistoryOptimized = true,
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };

        var body = DysonTurnSummarizer.FormatTurnBody(turn);
        if (!body.Contains("inspect auth", StringComparison.Ordinal)
            || !body.Contains("found JwtBearer", StringComparison.Ordinal)
            || !body.Contains("ReadFile Program.cs", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("FormatTurnBody must include instruction/assistant/tools.");
        }

        if (DysonTurnSummarizer.HasSummary(turn))
            throw new InvalidOperationException("HasSummary must be false before ContextSummary is set.");

        turn.ContextSummary = "Auth uses JwtBearer.";
        if (!DysonTurnSummarizer.HasSummary(turn))
            throw new InvalidOperationException("HasSummary must be true when ContextSummary is set.");

        var stub = DysonTurnSummarizer.FormatSummaryStub(turn);
        if (!stub.Contains($"[turnId={turn.Id:D}]", StringComparison.Ordinal)
            || !stub.Contains("[contextSummary]", StringComparison.Ordinal)
            || !stub.Contains("Auth uses JwtBearer.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("FormatSummaryStub must include turnId + summary.");
        }
    }

    private static void AssertPersistenceRoundTrip()
    {
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "long turn",
            AssistantText = "long reply",
            ContextSummary = "short stub",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };

        var entity = DysonTurnPersistence.ToEntity(turn, Guid.NewGuid(), sequence: 3);
        if (!string.Equals(entity.ContextSummary, "short stub", StringComparison.Ordinal))
            throw new InvalidOperationException("ToEntity must map ContextSummary.");
    }

    private static async Task AssertContextSummaryPersistsOnExistingRowUpsertAsync()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var subject = DysonTempDb.Subject("subject-a");
        var sessions = DysonTempDb.Sessions(accessor, subject);

        var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 0,
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "root",
            Title = "summary-upsert",
        }).ConfigureAwait(false);
        if (created.IsError)
            throw new InvalidOperationException(created.Error);

        var turnId = Guid.NewGuid();
        var completedUtc = DateTime.UtcNow;
        var seed = await sessions.UpsertTurnAsync(new DysonTurnEntity
        {
            Id = turnId,
            SessionId = created.Value,
            Sequence = 1,
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "inspect auth",
            ToolStateJson = "{}",
            ContextSummary = null,
            CompletedUtc = completedUtc,
        }).ConfigureAwait(false);
        if (seed.IsError)
            throw new InvalidOperationException(seed.Error);

        var upsertSummary = await sessions.UpsertTurnAsync(new DysonTurnEntity
        {
            Id = turnId,
            SessionId = created.Value,
            Sequence = 1,
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "inspect auth",
            ToolStateJson = "{}",
            ContextSummary = "compact facts",
            CompletedUtc = completedUtc,
        }).ConfigureAwait(false);
        if (upsertSummary.IsError)
            throw new InvalidOperationException(upsertSummary.Error);

        var full = await sessions.GetFullSessionAsync(created.Value).ConfigureAwait(false);
        if (full.IsError)
            throw new InvalidOperationException(full.Error);
        if (full.Value.Turns.Count != 1
            || !string.Equals(full.Value.Turns[0].ContextSummary, "compact facts", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Existing-row UpsertTurnAsync must persist ContextSummary.");
        }

        var upsertExcluded = await sessions.UpsertTurnAsync(new DysonTurnEntity
        {
            Id = turnId,
            SessionId = created.Value,
            Sequence = 1,
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "inspect auth",
            ToolStateJson = "{}",
            ContextSummary = "compact facts",
            IsExcludedFromContext = true,
            CompletedUtc = completedUtc,
        }).ConfigureAwait(false);
        if (upsertExcluded.IsError)
            throw new InvalidOperationException(upsertExcluded.Error);

        var afterExclude = await sessions.GetFullSessionAsync(created.Value).ConfigureAwait(false);
        if (afterExclude.IsError)
            throw new InvalidOperationException(afterExclude.Error);
        if (afterExclude.Value.Turns.Count != 1
            || !afterExclude.Value.Turns[0].IsExcludedFromContext
            || !string.Equals(afterExclude.Value.Turns[0].ContextSummary, "compact facts", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Existing-row UpsertTurnAsync must keep ContextSummary when IsExcludedFromContext is set.");
        }
    }

    private static void AssertTranscriptEmitsSummaryStub()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var summarized = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "VERBOSE_INSTRUCTION_SHOULD_NOT_APPEAR",
            AssistantText = "VERBOSE_ASSISTANT_SHOULD_NOT_APPEAR",
            ContextSummary = "compact facts only",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };
        session.AddTurnForTest(summarized);

        var completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: []);
        var json = completions.Messages.ToJsonString();
        if (!json.Contains($"[turnId={summarized.Id:D}]", StringComparison.Ordinal)
            || !json.Contains("[contextSummary]", StringComparison.Ordinal)
            || !json.Contains("compact facts only", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Completions must emit summary stub for summarized turns.");
        }

        if (json.Contains("VERBOSE_INSTRUCTION_SHOULD_NOT_APPEAR", StringComparison.Ordinal)
            || json.Contains("VERBOSE_ASSISTANT_SHOULD_NOT_APPEAR", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Completions must omit full body when ContextSummary is set.");
        }

        var responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: []);
        var responsesJson = responses.Input.ToJsonString();
        if (!responsesJson.Contains("compact facts only", StringComparison.Ordinal)
            || responsesJson.Contains("VERBOSE_INSTRUCTION_SHOULD_NOT_APPEAR", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Responses must emit summary stub and omit full body.");
        }
    }

    private static void AssertDropContextPrefersSummarize()
    {
        if (!DysonDropContextFlow.Instruction.Contains("SummarizeTurns", StringComparison.Ordinal)
            || !DysonDropContextFlow.Instruction.Contains("Prefer SummarizeTurns", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DropContext Instruction must prefer SummarizeTurns over DropTurnContext.");
        }

        var preamble = DysonAgentSystemPrompts.SharedPreamble;
        if (preamble.IndexOf("SummarizeTurns", StringComparison.Ordinal) < 0
            || preamble.IndexOf("DropTurnContext", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                "SharedPreamble must mention SummarizeTurns and DropTurnContext.");
        }

        var tools = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess).Tools;
        if (!tools.ContainsKey("SummarizeTurns"))
            throw new InvalidOperationException("Default catalog must register SummarizeTurns.");
    }

    private static async Task AssertSummarizeTurnsToolAsync()
    {
        var handler = new StubCompletionsHandler("Bullet facts about the turn.");
        using var http = new HttpClient(handler);
        var provider = CreateOpenAiProvider();
        var session = new StubSession(DysonAgentModes.Work, provider);
        session.ConfigureRootForTest();
        var executor = DysonWorkspaceTestFs.CreateExecutor(session, Path.GetTempPath(), http);

        var older = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "explore dead end",
            AssistantText = "lots of noise",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };
        session.AddTurnForTest(older);

        var excluded = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "already dropped",
            AssistantText = "gone",
            IsExcludedFromContext = true,
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };
        session.AddTurnForTest(excluded);

        var current = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "continue",
            StartedUtc = DateTime.UtcNow,
        };
        session.AddTurnForTest(current);

        var missingReason = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "s0",
            ToolName = "SummarizeTurns",
            Stage = 0,
            ArgumentsJson = $$"""{"turnIds":["{{older.Id}}"]}""",
        }).ConfigureAwait(false);
        if (!missingReason.IsError
            || missingReason.Content.IndexOf("reason", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "SummarizeTurns must require reason: " + missingReason.Content);
        }

        var result = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "s1",
            ToolName = "SummarizeTurns",
            Stage = 0,
            ArgumentsJson = $$"""{"turnIds":["{{older.Id}}","{{current.Id}}","{{excluded.Id}}","{{Guid.NewGuid()}}"],"reason":"compress"}""",
        }).ConfigureAwait(false);
        if (result.IsError)
            throw new InvalidOperationException("SummarizeTurns should succeed: " + result.Content);

        if (string.IsNullOrWhiteSpace(older.ContextSummary)
            || !older.ContextSummary.Contains("Bullet facts", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "SummarizeTurns must set ContextSummary on eligible turns: " + older.ContextSummary);
        }

        if (current.ContextSummary is not null)
            throw new InvalidOperationException("In-flight turn must not be summarized.");
        if (excluded.ContextSummary is not null)
            throw new InvalidOperationException("Excluded turn must be skipped.");

        if (result.Content.IndexOf("partial", StringComparison.OrdinalIgnoreCase) < 0
            || result.Content.IndexOf("in-flight", StringComparison.OrdinalIgnoreCase) < 0
            || result.Content.IndexOf("excluded", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "SummarizeTurns should report skipped in-flight/excluded: " + result.Content);
        }

        var log = $"Turn {older.Id:D} summarized, reason: compress";
        if (!session.SnapshotLog().Any(l => l.Equals(log, StringComparison.Ordinal)))
            throw new InvalidOperationException("SummarizeTurns must AppendLog: " + log);

        if (handler.CallCount < 1)
            throw new InvalidOperationException("SummarizeTurns must call Completions once per eligible turn.");
    }

    private static void AssertSummarizeClaimsAndGate()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var turnId = Guid.NewGuid();

        if (session.HasAnySummarizingTurn || session.IsSummarizingTurn(turnId))
            throw new InvalidOperationException("Fresh session must not be summarizing.");

        if (!session.TryBeginSummarizeTurn(turnId))
            throw new InvalidOperationException("TryBeginSummarizeTurn must succeed on first claim.");

        if (!session.HasAnySummarizingTurn || !session.IsSummarizingTurn(turnId))
            throw new InvalidOperationException("Claimed turn must set HasAny/IsSummarizing.");

        // Host PromptAsync enqueues while HasAnySummarizingTurn (same flag).
        if (!session.HasAnySummarizingTurn)
            throw new InvalidOperationException("Enqueue gate flag HasAnySummarizingTurn must be true while claimed.");

        if (session.TryBeginSummarizeTurn(turnId))
            throw new InvalidOperationException("Second claim on same turn must fail.");

        if (session.TryBeginSummarizeTurn(Guid.Empty))
            throw new InvalidOperationException("Empty turn id must not claim.");

        session.EndSummarizeTurn(turnId);
        if (session.HasAnySummarizingTurn || session.IsSummarizingTurn(turnId))
            throw new InvalidOperationException("EndSummarizeTurn must clear claim.");

        // Single-flight: second waiter blocks until Exit.
        session.EnterSummarizeGateAsync().GetAwaiter().GetResult();
        var entered = false;
        var waiter = Task.Run(async () =>
        {
            await session.EnterSummarizeGateAsync().ConfigureAwait(false);
            entered = true;
            session.ExitSummarizeGate();
        });

        Thread.Sleep(50);
        if (entered)
            throw new InvalidOperationException("Summarize gate must single-flight.");

        session.ExitSummarizeGate();
        if (!waiter.Wait(TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException("Summarize gate waiter did not proceed after Exit.");
        if (!entered)
            throw new InvalidOperationException("Summarize gate waiter must enter after Exit.");
    }

    private static async Task AssertSummarizeTurnsSkipsHasSummaryAndClaimAsync()
    {
        var handler = new StubCompletionsHandler("Should not be called.");
        using var http = new HttpClient(handler);
        var provider = CreateOpenAiProvider();
        var session = new StubSession(DysonAgentModes.Work, provider);
        session.ConfigureRootForTest();
        var executor = DysonWorkspaceTestFs.CreateExecutor(session, Path.GetTempPath(), http);

        var already = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "done",
            AssistantText = "done",
            ContextSummary = "already compact",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };
        session.AddTurnForTest(already);

        var claimed = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "in progress elsewhere",
            AssistantText = "noise",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };
        session.AddTurnForTest(claimed);

        // Pad so neither is the "current" in-flight turn.
        session.AddTurnForTest(new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "current",
            StartedUtc = DateTime.UtcNow,
        });

        if (!session.TryBeginSummarizeTurn(claimed.Id))
            throw new InvalidOperationException("Test setup must claim turn.");

        var result = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "s-dedupe",
            ToolName = "SummarizeTurns",
            Stage = 0,
            ArgumentsJson = $$"""{"turnIds":["{{already.Id}}","{{claimed.Id}}"],"reason":"dedupe"}""",
        }).ConfigureAwait(false);
        if (result.IsError)
            throw new InvalidOperationException("SummarizeTurns dedupe path should succeed: " + result.Content);

        if (result.Content.IndexOf("already summarized", StringComparison.OrdinalIgnoreCase) < 0
            || result.Content.IndexOf("already summarizing", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "SummarizeTurns must skip HasSummary and held claims: " + result.Content);
        }

        if (handler.CallCount != 0)
            throw new InvalidOperationException("SummarizeTurns must not LLM-summarize skipped turns.");

        if (!string.Equals(already.ContextSummary, "already compact", StringComparison.Ordinal))
            throw new InvalidOperationException("HasSummary turn must keep existing ContextSummary.");

        if (claimed.ContextSummary is not null)
            throw new InvalidOperationException("Claimed turn must not be overwritten.");

        session.EndSummarizeTurn(claimed.Id);
    }

    private static OpenAiCompatibleAgentProvider CreateOpenAiProvider()
    {
        var entity = new DysonModelProviderEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "test",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-test",
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

    private sealed class StubCompletionsHandler(string content) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var body = new JsonObject
            {
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["message"] = new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = content,
                        },
                    },
                },
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession : DysonAgentSession
    {
        public StubSession(string mode, DysonAgentProvider? provider = null)
            : base(mode, new DysonAgentSessionConfig(), provider ?? new StubProvider())
        {
        }

        public void ConfigureRootForTest() => ConfigureRootInterAgentTools();

        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
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
