using System.Text.Json;
using System.Text.Json.Nodes;
using DysonHarness;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// Voice sidecar facts that do not need a live model or audio: spawn rejection, comment markers,
/// memory in the prompt, recovery skip, and the ReadMetaAgentLog / EditMemory executor.
/// </summary>
public class DysonVoiceSidecarSliceTests
{
    [Fact]
    public void ValidateSubagentSpawn_rejects_sidecar_mode()
    {
        foreach (var parent in new[]
        {
            DysonAgentModes.MetaAgent,
            DysonAgentModes.MetaAgentDrone,
            DysonAgentModes.Work,
            DysonAgentModes.Drone,
        })
        {
            var result = DysonAgentSession.ValidateSubagentSpawn(parent, DysonAgentModes.VoiceSidecar);
            Assert.True(result.IsError, parent);
            Assert.Equal("Voice Mode Meta Sidecar is harness-created only.", result.Error);
        }
    }

    [Fact]
    public void Comment_markers_format_and_ride_on_the_delivered_comment()
    {
        var sidecar = DysonInjectedUserComments.FormatSidecarMessage("open the plan");
        Assert.Equal("[Message from voice sidecar]\n\nopen the plan", sidecar);
        var meta = DysonInjectedUserComments.FormatMetaMessage("done");
        Assert.Equal("[Message from Meta session]\n\ndone", meta);

        var turn = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal, Instruction = "work" };
        var queued = turn.EnqueueUserComment("open the plan", DysonInjectedUserComments.SidecarMarker);
        Assert.True(queued.IsSuccess, queued.IsError ? queued.Error : null);

        var comment = Assert.Single(DysonInjectedUserComments.From(turn.ReasoningLog));
        Assert.Equal(DysonInjectedUserComments.SidecarMarker, comment.Marker);
        Assert.Equal("open the plan", comment.Text);

        var array = new JsonArray();
        DysonInjectedUserComments.AppendMessages(array, [comment]);
        var message = Assert.Single(array).AsObject();
        Assert.Equal("user", message["role"]!.GetValue<string>());
        Assert.Equal(sidecar, message["content"]!.GetValue<string>());
        Assert.Equal(sidecar, DysonInjectedUserComments.FormatPlainText(turn.ReasoningLog));
    }

    [Fact]
    public void WithVoiceSidecarMemory_appends_only_when_non_empty()
    {
        var prompt = DysonAgentSystemPrompts.VoiceModeMetaSidecarDirective;
        var empty = DysonAgentSystemPrompts.WithVoiceSidecarMemory(prompt, DysonAgentModes.VoiceSidecar, "");
        Assert.Same(prompt, empty);
        var missing = DysonAgentSystemPrompts.WithVoiceSidecarMemory(prompt, DysonAgentModes.VoiceSidecar, null);
        Assert.Same(prompt, missing);

        var other = DysonAgentSystemPrompts.WithVoiceSidecarMemory(prompt, DysonAgentModes.MetaAgent, "notes");
        Assert.Same(prompt, other);

        var appended = DysonAgentSystemPrompts.WithVoiceSidecarMemory(prompt, DysonAgentModes.VoiceSidecar, "Ada");
        Assert.StartsWith(prompt, appended, StringComparison.Ordinal);
        Assert.EndsWith("\n\nYour memory (private notes, from ReadMemory):\nAda", appended, StringComparison.Ordinal);
    }

    [Fact]
    public void SetVoiceSidecarMemory_rebuilds_the_next_prompt()
    {
        var session = new StubSession(DysonAgentModes.VoiceSidecar);
        var before = session.SystemPrompt;
        Assert.DoesNotContain("Your memory", before, StringComparison.Ordinal);

        session.SetVoiceSidecarMemory("");
        Assert.Equal(before, session.SystemPrompt);

        session.SetVoiceSidecarMemory("Ada prefers short answers");
        Assert.Contains(DysonAgentSystemPrompts.VoiceModeMetaSidecarDirective, session.SystemPrompt, StringComparison.Ordinal);
        Assert.EndsWith(
            "\n\nYour memory (private notes, from ReadMemory):\nAda prefers short answers",
            session.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Equal("Ada prefers short answers", session.VoiceSidecarMemory);
    }

    [Fact]
    public void Sidecar_context_is_120000_and_summary_names_5000()
    {
        var sidecar = new StubSession(DysonAgentModes.VoiceSidecar)
        {
            MaxTargetContextTokens = 400_000,
            SlugDefaultMaxTargetContextTokens = 250_000,
        };
        Assert.Equal(120_000, sidecar.ResolveEffectiveMaxTargetContextTokens());
        Assert.Equal(DysonMaxTargetContextTokens.VoiceSidecarFixed, sidecar.ResolveEffectiveMaxTargetContextTokens());

        sidecar.MaxTargetContextTokens = 0;
        Assert.Equal(120_000, sidecar.ResolveEffectiveMaxTargetContextTokens());

        var meta = new StubSession(DysonAgentModes.MetaAgent);
        Assert.Equal(180_000, meta.ResolveEffectiveMaxTargetContextTokens());

        var instruction = DysonFullSummarizeFlow.SidecarInstruction;
        Assert.Contains("5,000", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("6,000", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("6,000", DysonFullSummarizeFlow.Instruction, StringComparison.Ordinal);

        var summary = DysonCompactionFlow.CreateSummaryTurn(DysonAgentModes.VoiceSidecar);
        Assert.Contains("5,000", summary.Instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("35,000", summary.Instruction, StringComparison.Ordinal);
        Assert.Contains("5,000", DysonCompactionFlow.SidecarFallbackSummaryReason, StringComparison.Ordinal);

        var work = new StubSession(DysonAgentModes.Work);
        var normal = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal };
        Assert.True(DysonCompactionFlow.CanCompact(sidecar, normal));
        Assert.False(DysonCompactionFlow.CanCompact(work, DysonCompactionFlow.CreateSummaryTurn(DysonAgentModes.Work)));
    }

    [Fact]
    public async Task Recovery_skips_sidecar_rows_without_writing()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var _keepAlive = conn;
        var sessions = DysonTempDb.Sessions(accessor);
        var recovery = new DysonSessionRecoveryService(sessions);

        var rootId = await CreateSessionAsync(sessions, 0, "root", DysonAgentModes.MetaAgent, null);
        var sidecarId = await CreateSessionAsync(
            sessions, 0, "sidecar", DysonAgentModes.VoiceSidecar, rootId);
        await SeedUnfinishedTurnAsync(sessions, sidecarId);

        var recovered = await recovery.RecoverAsync();
        Assert.True(recovered.IsSuccess, recovered.IsError ? recovered.Error : null);
        Assert.Equal(0, recovered.Value.TurnsRepaired);
        Assert.Equal(0, recovered.Value.DescendantsInterrupted);

        var full = await sessions.GetFullSessionAsync(sidecarId);
        Assert.True(full.IsSuccess, full.IsError ? full.Error : null);
        Assert.Equal(DysonSessionStatus.Active, full.Value.Session.Status);
        Assert.Null(Assert.Single(full.Value.Turns).CompletedUtc);
        Assert.Empty(full.Value.Logs);
    }

    [Fact]
    public async Task ReadMetaAgentLog_clamps_and_rejects_a_non_sidecar()
    {
        using var http = new HttpClient();
        var owner = new StubSession(DysonAgentModes.MetaAgent);
        owner.AppendLog("line-a");
        owner.AppendDisplayInfoTurn("older");
        owner.AppendDisplayInfoTurn(new string('p', 1200));
        owner.AppendDisplayInfoTurn("two");
        owner.AppendDisplayInfoTurn("four");

        var sidecar = new StubSession(DysonAgentModes.VoiceSidecar);
        sidecar.VoiceSidecarOwner = owner;
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(sidecar, Path.GetTempPath(), http);

        var zero = await executor.ExecuteAsync(Call("ReadMetaAgentLog", """{"maxLines":0}"""));
        Assert.False(zero.IsError, zero.Content);
        using (var doc = JsonDocument.Parse(zero.Content))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("lines").GetArrayLength());
            var posts = doc.RootElement.GetProperty("lastPosts");
            Assert.Equal(3, posts.GetArrayLength());
            Assert.Equal(1000, posts[0].GetString()!.Length);
            Assert.Equal("two", posts[1].GetString());
            Assert.Equal("four", posts[2].GetString());
        }

        var huge = await executor.ExecuteAsync(Call("ReadMetaAgentLog", """{"maxLines":1000}"""));
        Assert.False(huge.IsError, huge.Content);
        using (var doc = JsonDocument.Parse(huge.Content))
            Assert.Equal(1, doc.RootElement.GetProperty("lines").GetArrayLength());

        // A Meta catalog has no ReadMetaAgentLog, so the call dies at the catalog gate
        // before the sidecar-only message. The gate is the rejection.
        var meta = new StubSession(DysonAgentModes.MetaAgent);
        var denied = await DysonWorkspaceTestFs.CreateExecutorAsync(meta, Path.GetTempPath(), http);
        var rejected = await denied.ExecuteAsync(Call("ReadMetaAgentLog", """{"maxLines":10}"""));
        Assert.True(rejected.IsError);
        Assert.Contains(
            "Tool 'ReadMetaAgentLog' is not available in this session's catalog",
            rejected.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditMemory_applies_once_and_rejects_an_ambiguous_match()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var _keepAlive = conn;
        var subject = DysonTempDb.Subject("subject-a");
        var sessions = DysonTempDb.Sessions(accessor, subject);
        var memory = DysonTempDb.VoiceSidecarMemories(accessor, subject);
        var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 0,
            AgentMode = DysonAgentModes.VoiceSidecar,
            SystemPromptSnapshot = "sidecar",
        });
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

        var session = new StubSession(
            DysonAgentModes.VoiceSidecar,
            config: new DysonAgentSessionConfig { VoiceSidecarMemory = memory });
        session.SetPersistenceIdForTest(created.Value);
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, Path.GetTempPath(), new HttpClient());

        var seeded = await executor.ExecuteAsync(Call("EditMemory", """{"content":"alpha beta alpha"}"""));
        Assert.False(seeded.IsError, seeded.Content);
        Assert.Contains("\"version\":1", seeded.Content, StringComparison.Ordinal);
        Assert.Contains("alpha beta alpha", session.SystemPrompt, StringComparison.Ordinal);

        var ambiguous = await executor.ExecuteAsync(Call(
            "EditMemory",
            """{"old_text":"alpha","new_text":"ALPHA"}"""));
        Assert.True(ambiguous.IsError);
        Assert.Contains("unchanged", ambiguous.Content, StringComparison.Ordinal);

        var still = await memory.GetAsync(created.Value);
        Assert.True(still.IsSuccess, still.IsError ? still.Error : null);
        Assert.Equal("alpha beta alpha", still.Value.Content);
        Assert.Equal(1, still.Value.Version);

        var once = await executor.ExecuteAsync(Call(
            "EditMemory",
            """{"old_text":"beta","new_text":"BETA"}"""));
        Assert.False(once.IsError, once.Content);
        Assert.Contains("\"version\":2", once.Content, StringComparison.Ordinal);
        Assert.Contains("alpha BETA alpha", session.SystemPrompt, StringComparison.Ordinal);
    }

    private static DysonToolCall Call(string name, string args) => new()
    {
        CallId = Guid.NewGuid().ToString("N"),
        ToolName = name,
        Stage = 0,
        ArgumentsJson = args,
    };

    private static async Task<Guid> CreateSessionAsync(
        IDysonSessionRepository sessions,
        int runtimeId,
        string title,
        string agentMode,
        Guid? parentSessionId)
    {
        var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = runtimeId,
            ParentSessionId = parentSessionId,
            AgentMode = agentMode,
            Title = title,
            SystemPromptSnapshot = title,
        });
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        return created.Value;
    }

    private static async Task SeedUnfinishedTurnAsync(IDysonSessionRepository sessions, Guid sessionId)
    {
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "still talking",
            AssistantText = "partial",
            StartedUtc = DateTime.UtcNow,
        };
        var entity = DysonTurnPersistence.ToEntity(turn, sessionId, sequence: 1);
        var upsert = await sessions.UpsertTurnAsync(entity);
        Assert.True(upsert.IsSuccess, upsert.IsError ? upsert.Error : null);
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(
        string mode,
        DysonAgentSessionConfig? config = null) : DysonAgentSession(
        mode,
        config ?? new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void SetPersistenceIdForTest(Guid persistenceId) => SetPersistenceId(persistenceId);

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
