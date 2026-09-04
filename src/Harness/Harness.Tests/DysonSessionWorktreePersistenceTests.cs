using DysonHarness;

namespace Harness.Tests;

public sealed class DysonSessionWorktreePersistenceTests
{
    [Fact]
    public void Run()
    {
        AssertCreateEnabledRoundTrip();
        AssertDefaultCreateIsDisabledWithoutPath();
        AssertMetaSetsThenClearsLocation();
        AssertListHasWorktreeOnlyWhenPathNonEmpty();
        AssertTwoEnabledSessionsDoNotSharePath();
    }

    [Fact]
    public void DeleteSession_BlockedWhilePathSet_ThenSucceedsAfterClear()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var sessions = DysonTempDb.Sessions(accessor);

        var created = sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 1,
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "wt-delete",
        }).GetAwaiter().GetResult();
        if (created.IsError)
            throw new InvalidOperationException(created.Error);

        var set = sessions.UpdateSessionMetaAsync(new DysonSessionMetaUpdate
        {
            SessionId = created.Value,
            UpdateWorktreeLocation = true,
            WorktreeAbsolutePath = @"C:\tmp\dyson-wt-delete",
            WorktreeBranch = "dyson/abcd1234",
        }).GetAwaiter().GetResult();
        if (set.IsError)
            throw new InvalidOperationException(set.Error);

        var blocked = sessions.DeleteSessionAsync(created.Value).GetAwaiter().GetResult();
        if (!blocked.IsError)
            throw new InvalidOperationException("Delete must fail while WorktreeAbsolutePath is set.");
        if (blocked.Error != "Merge or delete this session's worktree before deleting the session.")
            throw new InvalidOperationException($"Unexpected delete error: {blocked.Error}");

        var stillThere = sessions.GetFullSessionAsync(created.Value).GetAwaiter().GetResult();
        if (stillThere.IsError)
            throw new InvalidOperationException("Session must remain after blocked delete.");

        var clear = sessions.UpdateSessionMetaAsync(new DysonSessionMetaUpdate
        {
            SessionId = created.Value,
            UpdateWorktreeLocation = true,
            WorktreeAbsolutePath = null,
            WorktreeBranch = null,
        }).GetAwaiter().GetResult();
        if (clear.IsError)
            throw new InvalidOperationException(clear.Error);

        var deleted = sessions.DeleteSessionAsync(created.Value).GetAwaiter().GetResult();
        if (deleted.IsError)
            throw new InvalidOperationException(deleted.Error);

        var gone = sessions.GetFullSessionAsync(created.Value).GetAwaiter().GetResult();
        if (!gone.IsError)
            throw new InvalidOperationException("Session must be gone after delete once path is cleared.");
    }

    private static void AssertCreateEnabledRoundTrip()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var sessions = DysonTempDb.Sessions(accessor);

        var created = sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 1,
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "wt",
            WorktreeEnabled = true,
        }).GetAwaiter().GetResult();
        if (created.IsError)
            throw new InvalidOperationException(created.Error);

        var full = sessions.GetFullSessionAsync(created.Value).GetAwaiter().GetResult();
        if (full.IsError)
            throw new InvalidOperationException(full.Error);

        var session = full.Value.Session;
        if (!session.WorktreeEnabled)
            throw new InvalidOperationException("WorktreeEnabled=true must round-trip.");
        if (session.WorktreeAbsolutePath is not null || session.WorktreeBranch is not null)
            throw new InvalidOperationException("Create without path/branch must persist nulls.");
    }

    private static void AssertDefaultCreateIsDisabledWithoutPath()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var sessions = DysonTempDb.Sessions(accessor);

        var created = sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 1,
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "wt-default",
        }).GetAwaiter().GetResult();
        if (created.IsError)
            throw new InvalidOperationException(created.Error);

        var full = sessions.GetFullSessionAsync(created.Value).GetAwaiter().GetResult();
        if (full.IsError)
            throw new InvalidOperationException(full.Error);

        var session = full.Value.Session;
        if (session.WorktreeEnabled)
            throw new InvalidOperationException("Default create must persist WorktreeEnabled=false.");
        if (session.WorktreeAbsolutePath is not null || session.WorktreeBranch is not null)
            throw new InvalidOperationException("Default create must persist null path/branch.");

        var listed = sessions.ListSessionsAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);
        var summary = listed.Value.Single(s => s.Id == created.Value);
        if (summary.WorktreeEnabled || summary.HasWorktree)
            throw new InvalidOperationException("Default list summary must be disabled without worktree.");
    }

    private static void AssertMetaSetsThenClearsLocation()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var sessions = DysonTempDb.Sessions(accessor);

        var created = sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 1,
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "wt-meta",
            WorktreeEnabled = true,
        }).GetAwaiter().GetResult();
        if (created.IsError)
            throw new InvalidOperationException(created.Error);

        var set = sessions.UpdateSessionMetaAsync(new DysonSessionMetaUpdate
        {
            SessionId = created.Value,
            UpdateWorktreeLocation = true,
            WorktreeAbsolutePath = @"C:\tmp\dyson-wt",
            WorktreeBranch = "dyson/abcd1234",
        }).GetAwaiter().GetResult();
        if (set.IsError)
            throw new InvalidOperationException(set.Error);

        var afterSet = sessions.GetFullSessionAsync(created.Value).GetAwaiter().GetResult();
        if (afterSet.IsError)
            throw new InvalidOperationException(afterSet.Error);
        if (afterSet.Value.Session.WorktreeAbsolutePath != @"C:\tmp\dyson-wt"
            || afterSet.Value.Session.WorktreeBranch != "dyson/abcd1234")
        {
            throw new InvalidOperationException("UpdateWorktreeLocation must persist path and branch.");
        }

        var listed = sessions.ListSessionsAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);
        if (!listed.Value.Single(s => s.Id == created.Value).HasWorktree)
            throw new InvalidOperationException("HasWorktree must be true when path is non-empty.");

        var clear = sessions.UpdateSessionMetaAsync(new DysonSessionMetaUpdate
        {
            SessionId = created.Value,
            UpdateWorktreeLocation = true,
            WorktreeAbsolutePath = null,
            WorktreeBranch = null,
        }).GetAwaiter().GetResult();
        if (clear.IsError)
            throw new InvalidOperationException(clear.Error);

        var afterClear = sessions.GetFullSessionAsync(created.Value).GetAwaiter().GetResult();
        if (afterClear.IsError)
            throw new InvalidOperationException(afterClear.Error);
        if (afterClear.Value.Session.WorktreeAbsolutePath is not null
            || afterClear.Value.Session.WorktreeBranch is not null)
        {
            throw new InvalidOperationException("UpdateWorktreeLocation with nulls must clear path/branch.");
        }

        var listedAfterClear = sessions.ListSessionsAsync().GetAwaiter().GetResult();
        if (listedAfterClear.IsError)
            throw new InvalidOperationException(listedAfterClear.Error);
        if (listedAfterClear.Value.Single(s => s.Id == created.Value).HasWorktree)
            throw new InvalidOperationException("HasWorktree must be false after path is cleared.");
    }

    private static void AssertListHasWorktreeOnlyWhenPathNonEmpty()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var sessions = DysonTempDb.Sessions(accessor);

        var enabled = sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 1,
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "wt-empty-path",
            WorktreeEnabled = true,
            WorktreeAbsolutePath = "",
            WorktreeBranch = "dyson/deadbeef",
        }).GetAwaiter().GetResult();
        if (enabled.IsError)
            throw new InvalidOperationException(enabled.Error);

        var listed = sessions.ListSessionsAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);

        var summary = listed.Value.Single(s => s.Id == enabled.Value);
        if (!summary.WorktreeEnabled)
            throw new InvalidOperationException("List must project WorktreeEnabled.");
        if (summary.HasWorktree)
            throw new InvalidOperationException("HasWorktree must be false for empty path.");
    }

    private static void AssertTwoEnabledSessionsDoNotSharePath()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var workDirs = DysonTempDb.WorkDirectories(accessor);
        var sessions = DysonTempDb.Sessions(accessor);

        var tmp = Path.Combine(Path.GetTempPath(), $"dyson-wd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var createdWd = workDirs.CreateAsync(tmp).GetAwaiter().GetResult();
            if (createdWd.IsError)
                throw new InvalidOperationException(createdWd.Error);

            var first = sessions.CreateSessionAsync(new DysonSessionCreateRequest
            {
                RuntimeId = 1,
                AgentMode = DysonAgentModes.Work,
                SystemPromptSnapshot = "wt-a",
                WorkDirectoryId = createdWd.Value,
                WorktreeEnabled = true,
            }).GetAwaiter().GetResult();
            if (first.IsError)
                throw new InvalidOperationException(first.Error);

            var second = sessions.CreateSessionAsync(new DysonSessionCreateRequest
            {
                RuntimeId = 2,
                AgentMode = DysonAgentModes.Work,
                SystemPromptSnapshot = "wt-b",
                WorkDirectoryId = createdWd.Value,
                WorktreeEnabled = true,
            }).GetAwaiter().GetResult();
            if (second.IsError)
                throw new InvalidOperationException(second.Error);

            var listed = sessions.ListSessionsAsync(createdWd.Value).GetAwaiter().GetResult();
            if (listed.IsError)
                throw new InvalidOperationException(listed.Error);
            if (listed.Value.Count != 2)
                throw new InvalidOperationException("Expected two sessions on the same work directory.");
            if (listed.Value.Any(s => !s.WorktreeEnabled))
                throw new InvalidOperationException("Both sessions must keep WorktreeEnabled.");
            if (listed.Value.Any(s => s.HasWorktree))
                throw new InvalidOperationException("Enabled sessions must not share a path until ensure.");

            var a = sessions.GetFullSessionAsync(first.Value).GetAwaiter().GetResult();
            var b = sessions.GetFullSessionAsync(second.Value).GetAwaiter().GetResult();
            if (a.IsError)
                throw new InvalidOperationException(a.Error);
            if (b.IsError)
                throw new InvalidOperationException(b.Error);
            if (a.Value.Session.WorktreeAbsolutePath is not null
                || b.Value.Session.WorktreeAbsolutePath is not null)
            {
                throw new InvalidOperationException("Both enabled sessions must keep null path until ensure.");
            }
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* ignore */ }
        }
    }
}
