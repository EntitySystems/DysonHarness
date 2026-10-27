using DysonHarness;
using Microsoft.EntityFrameworkCore;

namespace Harness.Tests;

/// <summary>
/// ponytail: deleting a work directory removes its Meta Agent tree (child, todo, log, turn)
/// and plans in one transaction; another session on it blocks with nothing partially deleted.
/// </summary>
public class DysonWorkDirectoryDeleteTests
{
    [Fact]
    public async Task Delete_removes_meta_agent_tree_todos_logs_turns_and_plans()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        await using var _ = connection;
        var seeded = await SeedAsync(accessor);

        var deleted = await DysonTempDb.WorkDirectories(accessor).DeleteAsync(seeded.WorkDirectoryId);

        Assert.True(deleted.IsSuccess, deleted.IsError ? deleted.Error : null);
        Assert.Equal((0, 0, 0, 0, 0, 0, 0), await CountAsync(accessor));
    }

    [Fact]
    public async Task Delete_with_other_session_fails_and_deletes_nothing()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        await using var _ = connection;
        var seeded = await SeedAsync(accessor);
        var work = await DysonTempDb.Sessions(accessor).CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 1,
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "work",
            WorkDirectoryId = seeded.WorkDirectoryId,
        });
        Assert.True(work.IsSuccess);

        var deleted = await DysonTempDb.WorkDirectories(accessor).DeleteAsync(seeded.WorkDirectoryId);

        Assert.True(deleted.IsError);
        Assert.Contains("1 session(s) still reference it", deleted.Error);
        Assert.Equal((3, 1, 1, 1, 1, 1, 1), await CountAsync(accessor));
    }

    private static async Task<(Guid WorkDirectoryId, Guid MetaId)> SeedAsync(DysonDbAccessor accessor)
    {
        var wd = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await accessor.RunAsync(async (db, ct) =>
        {
            db.WorkDirectories.Add(new DysonWorkDirectoryEntity
            {
                Id = wd,
                SubjectId = DysonSubjects.Local,
                Name = "delete-test",
                AbsolutePath = Path.Combine(Path.GetTempPath(), wd.ToString("N")),
                CreatedUtc = now,
                LastOpenedUtc = now,
            });
            await DysonDbAccessor.SaveChangesAsync(db, ct).ConfigureAwait(false);
        });

        var sessions = DysonTempDb.Sessions(accessor);
        var meta = Ok(await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 0,
            AgentMode = DysonAgentModes.MetaAgent,
            SystemPromptSnapshot = "meta",
            WorkDirectoryId = wd,
        }));
        var drone = Ok(await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 1,
            ParentSessionId = meta,
            AgentMode = DysonAgentModes.MetaAgentDrone,
            SystemPromptSnapshot = "drone",
            WorkDirectoryId = wd,
        }));
        Assert.True((await sessions.UpdateSessionMetaAsync(new DysonSessionMetaUpdate
        {
            SessionId = drone,
            UpdateWorktreeLocation = true,
            WorktreeAbsolutePath = Path.Combine(Path.GetTempPath(), "wt-" + drone.ToString("N")),
            WorktreeBranch = "dyson/test",
        })).IsSuccess);
        Assert.True((await sessions.CreateTodoAsync(new DysonSessionTodoCreateRequest
        {
            SessionId = drone,
            TaskCode = "T1",
            DisplayName = "Todo",
        })).IsSuccess);
        Assert.True((await sessions.AppendLogAsync(new DysonSessionLogEntry
        {
            SessionId = meta,
            Kind = DysonSessionLogKind.SessionCreated.ToString(),
            PayloadJson = "{}",
        })).IsSuccess);
        Assert.True((await sessions.UpsertTurnAsync(new DysonTurnEntity
        {
            SessionId = drone,
            Sequence = 1,
            Kind = DysonAgentTurnKind.Normal,
            ToolStateJson = "{}",
            Instruction = "go",
        })).IsSuccess);
        Assert.True((await DysonTempDb.Plans(accessor)
            .CreateAsync(wd, DysonPlanKind.MetaPlan, "Plan", "# plan", planRelativePath: null)).IsSuccess);
        Assert.True((await DysonTempDb.ScratchFiles(accessor)
            .CreateAsync(wd, "Scratch", "# scratch")).IsSuccess);

        return (wd, meta);
    }

    private static Guid Ok(Result<Guid, string> created)
    {
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        return created.Value;
    }

    private static Task<(int Sessions, int Turns, int Logs, int Todos, int Plans, int ScratchFiles, int WorkDirectories)> CountAsync(
        DysonDbAccessor accessor) =>
        accessor.RunAsync(async (db, ct) => (
            await db.Sessions.CountAsync(ct),
            await db.Turns.CountAsync(ct),
            await db.SessionLogs.CountAsync(ct),
            await db.SessionTodos.CountAsync(ct),
            await db.Plans.CountAsync(ct),
            await db.MetaScratchFiles.CountAsync(ct),
            await db.WorkDirectories.CountAsync(ct)));
}
