using DysonHarness;
using Harness.UI.Demo;

namespace Harness.Tests;

/// <summary>Root Meta Agent chat: load-time trim keeps the newest 100 interactions and the compaction tail.</summary>
public class DysonMetaChatTrimTests
{
    private static DysonTurnEntity Row(Guid sessionId, int seq, DysonAgentTurnKind kind, bool excluded, string? text = "hi") =>
        new()
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Sequence = seq,
            Kind = kind,
            Instruction = kind == DysonAgentTurnKind.Normal ? text : null,
            AssistantText = kind == DysonAgentTurnKind.Normal ? null : text,
            ToolStateJson = "{}",
            IsExcludedFromContext = excluded,
            CompletedUtc = DateTime.UtcNow,
        };

    /// <summary>
    /// <paramref name="before"/> excluded Normal turns, a FullSummarize boundary, then <paramref name="after"/>
    /// in-context Normal turns. Returns all rows in order.
    /// </summary>
    private static async Task<(IDysonSessionRepository Sessions, Guid SessionId, List<DysonTurnEntity> Rows, string Dir)> SeedAsync(
        DysonDbAccessor accessor, string mode, int before, int after)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dyson-meta-trim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sessions = DysonTempDb.Sessions(accessor);
        var wd = await DysonTempDb.WorkDirectories(accessor).CreateAsync(dir);
        Assert.True(wd.IsSuccess, wd.IsError ? wd.Error : null);
        var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            AgentMode = mode,
            WorkDirectoryId = wd.Value,
            SystemPromptSnapshot = "root",
            Title = "trim",
        });
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

        var rows = new List<DysonTurnEntity>();
        var seq = 1;
        for (var i = 0; i < before; i++)
            rows.Add(Row(created.Value, seq++, DysonAgentTurnKind.Normal, excluded: true, $"old {i}"));
        rows.Add(Row(created.Value, seq++, DysonAgentTurnKind.FullSummarize, excluded: false, "summary"));
        for (var i = 0; i < after; i++)
            rows.Add(Row(created.Value, seq++, DysonAgentTurnKind.Normal, excluded: false, $"new {i}"));

        foreach (var row in rows)
        {
            var upsert = await sessions.UpsertTurnAsync(row);
            Assert.True(upsert.IsSuccess, upsert.IsError ? upsert.Error : null);
        }

        return (sessions, created.Value, rows, dir);
    }

    private static async Task<DemoDysonAgentSession> LoadAsync(IDysonSessionRepository sessions, Guid id, string dir)
    {
        var loaded = await DemoDysonAgentSession.LoadAsync(
            sessions, id, new DemoDysonAgentProvider(provider: null, slug: null),
            appendResumeLog: false, workDirectoryAbsolutePath: dir);
        Assert.True(loaded.IsSuccess, loaded.IsError ? loaded.Error : null);
        await loaded.Value.MetaChatTrimCompletion;
        return loaded.Value;
    }

    private static async Task<List<Guid>> PersistedTurnIdsAsync(IDysonSessionRepository sessions, Guid id)
    {
        var full = await sessions.GetFullSessionAsync(id);
        Assert.True(full.IsSuccess, full.IsError ? full.Error : null);
        return full.Value.Turns.OrderBy(t => t.Sequence).Select(t => t.Id).ToList();
    }

    [Fact]
    public async Task Load_deletes_turns_older_than_window_and_their_logs_keeps_pending_question()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var keepAlive = conn;
        // 150 old + summary + 10 new: window = 10 new + 90 old, so old[0..59] are trimmable.
        var (sessions, id, rows, dir) = await SeedAsync(accessor, DysonAgentModes.MetaAgent, before: 150, after: 10);

        // old[5] becomes a pending question card: never deleted.
        var question = rows[5];
        question.Kind = DysonAgentTurnKind.DisplayInfo;
        question.Instruction = null;
        question.AssistantText = "Pick one";
        question.UserQuestionJson = DysonUserQuestionSerializer.Serialize(
            new DysonUserQuestion(Guid.NewGuid(), "Pick one", ["a", "b"], false, false, null));
        Assert.True((await sessions.UpsertTurnAsync(question)).IsSuccess);

        var turnLog = DysonSessionLogPayload.CreateEntry(
            id, DysonSessionLogKind.TurnStarted, new DysonSessionLogTurnStarted(rows[0].Id, DysonAgentTurnKind.Normal), rows[0].Id);
        var sessionLog = DysonSessionLogPayload.CreateEntry(
            id, DysonSessionLogKind.LogLine, new DysonSessionLogLogLine("kept"));
        Assert.True((await sessions.AppendLogAsync(turnLog)).IsSuccess);
        Assert.True((await sessions.AppendLogAsync(sessionLog)).IsSuccess);

        try
        {
            var session = await LoadAsync(sessions, id, dir);

            var expected = rows.Skip(60).Select(r => r.Id).Prepend(question.Id).ToList();
            Assert.Equal(expected, session.Turns.Select(t => t.Id).ToList());
            Assert.Equal(expected, await PersistedTurnIdsAsync(sessions, id));

            var logs = (await sessions.GetFullSessionAsync(id)).Value.Logs;
            Assert.DoesNotContain(logs, l => l.Id == turnLog.Id);
            Assert.Contains(logs, l => l.Id == sessionLog.Id);

            // Idempotent: a second load finds nothing left to trim.
            var again = await LoadAsync(sessions, id, dir);
            Assert.Equal(expected, again.Turns.Select(t => t.Id).ToList());
            Assert.Equal(expected, await PersistedTurnIdsAsync(sessions, id));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Load_keeps_compaction_boundary_and_every_later_turn()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var keepAlive = conn;
        // 120 in-context turns after the summary: more than the window, yet none may go.
        var (sessions, id, rows, dir) = await SeedAsync(accessor, DysonAgentModes.MetaAgent, before: 30, after: 120);
        try
        {
            await LoadAsync(sessions, id, dir);

            Assert.Equal(rows.Skip(30).Select(r => r.Id).ToList(), await PersistedTurnIdsAsync(sessions, id));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Load_leaves_non_meta_sessions_alone()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var keepAlive = conn;
        var (sessions, id, rows, dir) = await SeedAsync(accessor, DysonAgentModes.Work, before: 150, after: 10);
        try
        {
            var session = await LoadAsync(sessions, id, dir);

            Assert.Equal(rows.Count, session.Turns.Count);
            Assert.Equal(rows.Select(r => r.Id).ToList(), await PersistedTurnIdsAsync(sessions, id));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Window_without_compaction_trims_nothing()
    {
        var turns = Enumerable.Range(0, 150)
            .Select(i => new DysonAgentTurn
            {
                Kind = DysonAgentTurnKind.Normal,
                Instruction = $"m{i}",
                IsExcludedFromContext = true,
                CompletedUtc = DateTime.UtcNow,
            })
            .ToList();

        Assert.Equal(50, DysonMetaChatWindow.DisplayStart(turns));
        Assert.Empty(DysonMetaChatWindow.SelectTrimmable(turns));
    }
}
