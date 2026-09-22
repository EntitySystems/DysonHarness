using DysonHarness;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// ponytail: 15-turn tick, 40-turn hard window, MetaMaintenance enqueue, Work-mode no-op.
/// </summary>
public class DysonMetaMaintenanceTickTests
{
    [Fact]
    public void ShouldTick_false_at_14_true_at_15()
    {
        Assert.Equal(40, DysonMetaMaintenanceTick.MaxTurns);
        Assert.Equal(15, DysonMetaMaintenanceTick.IntervalTurns);
        Assert.False(DysonMetaMaintenanceTick.ShouldTick(14));
        Assert.True(DysonMetaMaintenanceTick.ShouldTick(15));
        Assert.True(DysonMetaMaintenanceTick.ShouldTick(16));
        Assert.True(DysonMetaMaintenanceTick.AppliesTo(DysonAgentModes.MetaAgent));
        Assert.False(DysonMetaMaintenanceTick.AppliesTo(DysonAgentModes.Work));
        Assert.False(DysonMetaMaintenanceTick.AppliesTo(DysonAgentModes.MetaAgentDrone));
    }

    [Fact]
    public async Task Turn_41_with_no_tick_due_evicts_nothing()
    {
        var session = SeedMeta(41);
        session.TurnsSinceMetaMaintenance = 10;
        var last = session.Turns[^1];
        var result = await session.ApplyMetaMaintenanceTickAsync(last);
        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
        Assert.Equal(41, session.Turns.Count);
        Assert.Equal(11, session.TurnsSinceMetaMaintenance);
        Assert.False(session.TryDequeuePendingTurn(out _));
        Assert.Single(DysonMetaMaintenanceTick.SelectEvicted(session));
    }

    [Fact]
    public void SelectEvicted_at_55_keeps_newest_40()
    {
        var session = SeedMeta(55);
        var evicted = DysonMetaMaintenanceTick.SelectEvicted(session);
        Assert.Equal(15, evicted.Count);
        for (var i = 0; i < 15; i++)
            Assert.Same(session.Turns[i], evicted[i]);

        DysonMetaMaintenanceTick.Evict(session, evicted);
        Assert.Equal(40, session.Turns.Count);
        Assert.Equal("t-15", session.Turns[0].Instruction);
        Assert.Equal("t-54", session.Turns[^1].Instruction);
        Assert.Contains(
            session.SnapshotLog(),
            line => line.Contains("deleted, reason: meta maintenance", StringComparison.Ordinal));
    }

    [Fact]
    public void SelectEvicted_skips_in_flight_newest_summarize_and_pending_maintenance()
    {
        var session = new StubSession(DysonAgentModes.MetaAgent);
        var summarize = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.FullSummarize,
            Instruction = "summary",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };
        session.AddTurnForTest(summarize);
        for (var i = 0; i < 42; i++)
            session.AddTurnForTest(Completed($"live-{i}"));

        var inFlight = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "running",
            StartedUtc = DateTime.UtcNow,
        };
        session.AddTurnForTest(inFlight);
        var pendingMaint = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.MetaMaintenance,
            Instruction = DysonMetaMaintenanceTick.Instruction,
            StartedUtc = DateTime.UtcNow,
        };
        session.AddTurnForTest(pendingMaint);

        using (session.BeginInFlightPrompt(inFlight))
        {
            var evicted = DysonMetaMaintenanceTick.SelectEvicted(session);
            Assert.DoesNotContain(summarize, evicted);
            Assert.DoesNotContain(inFlight, evicted);
            Assert.DoesNotContain(pendingMaint, evicted);
            Assert.Equal(2, evicted.Count);
        }
    }

    [Fact]
    public async Task Tick_on_15_enqueues_exactly_one_maintenance_turn()
    {
        var session = SeedMeta(20);
        var last = session.Turns[^1];

        for (var i = 0; i < 14; i++)
        {
            var result = await session.ApplyMetaMaintenanceTickAsync(last);
            Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
            Assert.False(session.TryDequeuePendingTurn(out _));
        }

        var tick = await session.ApplyMetaMaintenanceTickAsync(last);
        Assert.True(tick.IsSuccess, tick.IsError ? tick.Error : null);
        Assert.True(session.TryDequeuePendingTurn(out var maint));
        Assert.Equal(DysonAgentTurnKind.MetaMaintenance, maint.Kind);
        Assert.Equal(DysonMetaMaintenanceTick.Instruction, maint.Instruction);
        Assert.Equal(0, session.TurnsSinceMetaMaintenance);
        Assert.False(session.TryDequeuePendingTurn(out _));

        var again = await session.ApplyMetaMaintenanceTickAsync(last);
        Assert.True(again.IsSuccess, again.IsError ? again.Error : null);
        Assert.False(session.TryDequeuePendingTurn(out _));
        Assert.Equal(1, session.TurnsSinceMetaMaintenance);
    }

    [Fact]
    public async Task MetaMaintenance_turn_does_not_increment_the_counter()
    {
        var session = SeedMeta(15);
        session.TurnsSinceMetaMaintenance = 14;
        var last = session.Turns[^1];
        var tick = await session.ApplyMetaMaintenanceTickAsync(last);
        Assert.True(tick.IsSuccess, tick.IsError ? tick.Error : null);
        Assert.True(session.TryDequeuePendingTurn(out var maint));
        Assert.Equal(0, session.TurnsSinceMetaMaintenance);

        maint.CompletedUtc = DateTime.UtcNow;
        session.AddTurnForTest(maint);
        var after = await session.ApplyMetaMaintenanceTickAsync(maint);
        Assert.True(after.IsSuccess, after.IsError ? after.Error : null);
        Assert.Equal(0, session.TurnsSinceMetaMaintenance);
        Assert.False(session.TryDequeuePendingTurn(out _));
    }

    [Fact]
    public async Task Work_mode_at_60_turns_is_untouched()
    {
        var session = Seed(DysonAgentModes.Work, 60);
        var last = session.Turns[^1];
        for (var i = 0; i < 20; i++)
        {
            var result = await session.ApplyMetaMaintenanceTickAsync(last);
            Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
        }

        Assert.Equal(60, session.Turns.Count);
        Assert.Equal(0, session.TurnsSinceMetaMaintenance);
        Assert.False(session.TryDequeuePendingTurn(out _));
        Assert.Empty(DysonMetaMaintenanceTick.SelectEvicted(session));
    }

    [Fact]
    public async Task Tick_at_55_trims_memory_and_repository_to_newest_40()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Sessions(accessor);

        var created = await store.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 0,
            AgentMode = DysonAgentModes.MetaAgent,
            SystemPromptSnapshot = "meta",
        });
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

        var session = new StubSession(DysonAgentModes.MetaAgent);
        session.BindForTest(created.Value, store);

        for (var i = 0; i < 55; i++)
        {
            var turn = Completed($"t-{i}");
            session.AddTurnForTest(turn);
            var upsert = await store.UpsertTurnAsync(DysonTurnPersistence.ToEntity(turn, created.Value, i));
            Assert.True(upsert.IsSuccess, upsert.IsError ? upsert.Error : null);
        }

        var last = session.Turns[^1];
        session.TurnsSinceMetaMaintenance = 14;
        var tick = await session.ApplyMetaMaintenanceTickAsync(last);
        Assert.True(tick.IsSuccess, tick.IsError ? tick.Error : null);

        Assert.Equal(40, session.Turns.Count);
        Assert.Equal("t-15", session.Turns[0].Instruction);
        Assert.Equal("t-54", session.Turns[^1].Instruction);
        Assert.True(session.TryDequeuePendingTurn(out var maint));
        Assert.Equal(DysonAgentTurnKind.MetaMaintenance, maint.Kind);

        var full = await store.GetFullSessionAsync(created.Value);
        Assert.True(full.IsSuccess, full.IsError ? full.Error : null);
        Assert.Equal(40, full.Value.Turns.Count);
        Assert.Equal(session.Turns[0].Id, full.Value.Turns[0].Id);
        Assert.Equal(session.Turns[^1].Id, full.Value.Turns[^1].Id);
        foreach (var live in session.Turns)
            Assert.Contains(full.Value.Turns, row => row.Id == live.Id);

        var maxSequence = full.Value.Turns.Max(t => t.Sequence);
        var fresh = Completed("t-after-tick");
        session.AddTurnForTest(fresh);
        var afterTick = await store.UpsertTurnAsync(
            DysonTurnPersistence.ToEntity(fresh, created.Value, session.Turns.Count - 1));
        Assert.True(afterTick.IsSuccess, afterTick.IsError ? afterTick.Error : null);

        var reloaded = await store.GetFullSessionAsync(created.Value);
        Assert.True(reloaded.IsSuccess, reloaded.IsError ? reloaded.Error : null);
        Assert.Equal(41, reloaded.Value.Turns.Count);
        var stored = Assert.Single(reloaded.Value.Turns, t => t.Id == fresh.Id);
        Assert.Equal(maxSequence + 1, stored.Sequence);
    }

    private static StubSession SeedMeta(int count) => Seed(DysonAgentModes.MetaAgent, count);

    private static StubSession Seed(string mode, int count)
    {
        var session = new StubSession(mode);
        for (var i = 0; i < count; i++)
            session.AddTurnForTest(Completed($"t-{i}"));
        return session;
    }

    private static DysonAgentTurn Completed(string instruction) =>
        new()
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = instruction,
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode) : DysonAgentSession(
        mode,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

        public void BindForTest(Guid persistenceId, IDysonSessionRepository store)
        {
            PersistenceId = persistenceId;
            SessionStore = store;
        }

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
