using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: append ids, newest-first keyset paging, subject filter, prune keeps newest N.
/// </summary>
public class DysonFileChangeRepositoryTests
{
    [Fact]
    public async Task Append_returns_increasing_ids_and_list_pages_newest_first()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var sessions = DysonTempDb.Sessions(accessor);
            var changes = DysonTempDb.FileChanges(accessor);
            var root = await CreateSessionAsync(sessions, "root");

            var first = await changes.AppendAsync(Row(root, "a.txt"));
            AssertOk(first);
            var second = await changes.AppendAsync(Row(root, "b.txt"));
            AssertOk(second);
            var third = await changes.AppendAsync(Row(root, "c.txt"));
            AssertOk(third);

            Assert.True(first.Value > 0);
            Assert.Equal(first.Value + 1, second.Value);
            Assert.Equal(second.Value + 1, third.Value);

            var page = await changes.ListAsync(root, beforeId: null, take: 2);
            AssertOk(page);
            Assert.Equal([third.Value, second.Value], page.Value.Select(x => x.Id).ToArray());
            Assert.Equal(DateTimeKind.Utc, page.Value[0].CreatedUtc.Kind);

            var older = await changes.ListAsync(root, beforeId: second.Value, take: 10);
            AssertOk(older);
            Assert.Equal(first.Value, Assert.Single(older.Value).Id);

            var got = await changes.GetAsync(second.Value);
            AssertOk(got);
            Assert.Equal("b.txt", got.Value!.RelativePath);

            var missing = await changes.GetAsync(third.Value + 100);
            AssertOk(missing);
            Assert.Null(missing.Value);
        }
        finally
        {
            DeleteTemp(path);
        }
    }

    [Fact]
    public async Task Subject_filter_hides_other_subject_rows()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var subject = DysonTempDb.Subject();
            var sessions = DysonTempDb.Sessions(accessor, subject);
            var changes = DysonTempDb.FileChanges(accessor, subject);
            var root = await CreateSessionAsync(sessions, "root");

            var created = await changes.AppendAsync(Row(root, "a.txt"));
            AssertOk(created);

            subject.SubjectId = "other-subject";
            var foreign = await changes.ListAsync(root, beforeId: null, take: 10);
            AssertOk(foreign);
            Assert.Empty(foreign.Value);

            var hidden = await changes.GetAsync(created.Value);
            AssertOk(hidden);
            Assert.Null(hidden.Value);

            var denied = await changes.AppendAsync(Row(root, "b.txt"));
            Assert.True(denied.IsError);
            Assert.Equal($"Session '{root}' not found.", denied.Error);

            subject.SubjectId = DysonSubjects.Local;
            var still = await changes.ListAsync(root, beforeId: null, take: 10);
            AssertOk(still);
            Assert.Equal(created.Value, Assert.Single(still.Value).Id);
        }
        finally
        {
            DeleteTemp(path);
        }
    }

    [Fact]
    public async Task Prune_keeps_only_newest_n()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var sessions = DysonTempDb.Sessions(accessor);
            var changes = DysonTempDb.FileChanges(accessor, retainPerRoot: 3);
            var root = await CreateSessionAsync(sessions, "root");
            var other = await CreateSessionAsync(sessions, "other");

            // Prune fires when the autoincrement id is a multiple of min(50, N). Ids are shared across
            // roots, so pad with a throwaway session until the next append lands on that stride.
            var stride = Math.Min(50, 3);
            ulong last = 0;
            for (var i = 0; i < 5; i++)
            {
                var appended = await changes.AppendAsync(Row(root, $"f{i}.txt"));
                AssertOk(appended);
                last = appended.Value;
                if (last % (ulong)stride == 0)
                    break;
            }

            Assert.Equal(0UL, last % (ulong)stride);

            var otherRow = await changes.AppendAsync(Row(other, "keep.txt"));
            AssertOk(otherRow);

            var listed = await changes.ListAsync(root, beforeId: null, take: 50);
            AssertOk(listed);
            Assert.Equal(3, listed.Value.Count);
            Assert.Equal(last, listed.Value[0].Id);
            Assert.True(listed.Value.All(x => x.Id > last - 3));

            var kept = await changes.GetAsync(otherRow.Value);
            AssertOk(kept);
            Assert.NotNull(kept.Value);
        }
        finally
        {
            DeleteTemp(path);
        }
    }

    [Fact]
    public async Task Deleting_sessions_nulls_writer_and_cascades_root_rows()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var sessions = DysonTempDb.Sessions(accessor);
            var changes = DysonTempDb.FileChanges(accessor);
            var root = await CreateSessionAsync(sessions, "root");
            var child = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
            {
                RuntimeId = 1,
                AgentMode = DysonAgentModes.Work,
                SystemPromptSnapshot = "test",
                Title = "child",
                ParentSessionId = root,
            });
            Assert.True(child.IsSuccess, child.IsError ? child.Error : null);

            var row = Row(root, "a.txt");
            row.SessionId = child.Value;
            var appended = await changes.AppendAsync(row);
            AssertOk(appended);

            var deletedChild = await sessions.DeleteSessionAsync(child.Value);
            Assert.True(deletedChild.IsSuccess, deletedChild.IsError ? deletedChild.Error : null);
            var kept = await changes.GetAsync(appended.Value);
            AssertOk(kept);
            Assert.NotNull(kept.Value);
            Assert.Null(kept.Value!.SessionId);

            var deletedRoot = await sessions.DeleteSessionAsync(root);
            Assert.True(deletedRoot.IsSuccess, deletedRoot.IsError ? deletedRoot.Error : null);
            var gone = await changes.GetAsync(appended.Value);
            AssertOk(gone);
            Assert.Null(gone.Value);
        }
        finally
        {
            DeleteTemp(path);
        }
    }

    private static DysonFileChangeEntity Row(Guid root, string path) => new()
    {
        RootSessionId = root,
        RelativePath = path,
        RootPath = "/work",
        Kind = DysonFileChangeKind.Edited,
        ToolName = "WriteFile",
        StartLine = 1,
    };

    private static async Task<Guid> CreateSessionAsync(DysonSessionRepository sessions, string title)
    {
        var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 1,
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "test",
            Title = title,
        });
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        return created.Value;
    }

    private static void DeleteTemp(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static void AssertOk<T>(Result<T, string> result) =>
        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
}
