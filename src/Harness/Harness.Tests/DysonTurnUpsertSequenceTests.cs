using DysonHarness;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

public class DysonTurnUpsertSequenceTests
{
    [Fact]
    public async Task Insert_collision_allocates_max_plus_one_and_update_does_not_steal_sibling()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var sessions = DysonTempDb.Sessions(accessor);

        var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 0,
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "root",
        });
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

        var sessionId = created.Value;
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        var first = await sessions.UpsertTurnAsync(Turn(firstId, sessionId, 0, "first"));
        Assert.True(first.IsSuccess, first.IsError ? first.Error : null);

        var collision = await sessions.UpsertTurnAsync(Turn(secondId, sessionId, 0, "second"));
        Assert.True(collision.IsSuccess, collision.IsError ? collision.Error : null);

        var afterInsert = await sessions.GetFullSessionAsync(sessionId);
        Assert.True(afterInsert.IsSuccess, afterInsert.IsError ? afterInsert.Error : null);
        Assert.Equal(2, afterInsert.Value.Turns.Count);
        Assert.Equal(0, afterInsert.Value.Turns.Single(t => t.Id == firstId).Sequence);
        Assert.Equal(1, afterInsert.Value.Turns.Single(t => t.Id == secondId).Sequence);

        var steal = await sessions.UpsertTurnAsync(Turn(firstId, sessionId, 1, "first-updated"));
        Assert.True(steal.IsSuccess, steal.IsError ? steal.Error : null);

        var afterSteal = await sessions.GetFullSessionAsync(sessionId);
        Assert.True(afterSteal.IsSuccess, afterSteal.IsError ? afterSteal.Error : null);
        var kept = afterSteal.Value.Turns.Single(t => t.Id == firstId);
        var sibling = afterSteal.Value.Turns.Single(t => t.Id == secondId);
        Assert.Equal("first-updated", kept.Instruction);
        Assert.Equal(0, kept.Sequence);
        Assert.Equal(1, sibling.Sequence);
        Assert.Equal("second", sibling.Instruction);

        var move = await sessions.UpsertTurnAsync(Turn(firstId, sessionId, 4, "first-moved"));
        Assert.True(move.IsSuccess, move.IsError ? move.Error : null);

        var afterMove = await sessions.GetFullSessionAsync(sessionId);
        Assert.True(afterMove.IsSuccess, afterMove.IsError ? afterMove.Error : null);
        Assert.Equal(0, afterMove.Value.Turns.Single(t => t.Id == firstId).Sequence);
        Assert.Equal("first-moved", afterMove.Value.Turns.Single(t => t.Id == firstId).Instruction);
        Assert.Equal(1, afterMove.Value.Turns.Single(t => t.Id == secondId).Sequence);
    }

    [Fact]
    public async Task Insert_into_sequence_hole_appends_at_max_plus_one()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var sessions = DysonTempDb.Sessions(accessor);

        var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 0,
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "root",
        });
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

        var sessionId = created.Value;
        var lowId = Guid.NewGuid();
        var highId = Guid.NewGuid();
        var holeId = Guid.NewGuid();

        var low = await sessions.UpsertTurnAsync(Turn(lowId, sessionId, 0, "low"));
        Assert.True(low.IsSuccess, low.IsError ? low.Error : null);
        var high = await sessions.UpsertTurnAsync(Turn(highId, sessionId, 2, "high"));
        Assert.True(high.IsSuccess, high.IsError ? high.Error : null);

        var intoHole = await sessions.UpsertTurnAsync(Turn(holeId, sessionId, 1, "hole"));
        Assert.True(intoHole.IsSuccess, intoHole.IsError ? intoHole.Error : null);

        var full = await sessions.GetFullSessionAsync(sessionId);
        Assert.True(full.IsSuccess, full.IsError ? full.Error : null);
        Assert.Equal(0, full.Value.Turns.Single(t => t.Id == lowId).Sequence);
        Assert.Equal(2, full.Value.Turns.Single(t => t.Id == highId).Sequence);
        Assert.Equal(3, full.Value.Turns.Single(t => t.Id == holeId).Sequence);
    }

    private static DysonTurnEntity Turn(Guid id, Guid sessionId, int sequence, string instruction) =>
        new()
        {
            Id = id,
            SessionId = sessionId,
            Sequence = sequence,
            Kind = DysonAgentTurnKind.Normal,
            Instruction = instruction,
            ToolStateJson = "{}",
            CreatedUtc = DateTime.UtcNow,
        };
}
