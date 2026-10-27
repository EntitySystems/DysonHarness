using DysonHarness;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harness.Tests;

/// <summary>
/// ponytail: CRUD + UpdatedUtc bump, workdir scoping, 131072 char cap, list order
/// (indexed first, then unindexed by UpdatedUtc desc, Id desc), dense MoveAsync, validation.
/// </summary>
public class DysonMetaScratchFileRepositoryTests
{
    [Fact]
    public async Task Create_Get_Update_Delete_RoundTrip()
    {
        await using var fixture = await ScratchFixture.CreateAsync();
        var files = fixture.Files;
        var wd = fixture.WorkDirectoryId;

        var created = await files.CreateAsync(wd, "  Notes  ", "hello");
        AssertOk(created);
        Assert.True(created.Value > 0);

        var got = await files.GetAsync(created.Value, wd);
        AssertOk(got);
        Assert.Equal("Notes", got.Value.Title);
        Assert.Equal("hello", got.Value.Content);
        Assert.Equal(5, got.Value.ContentLength);
        Assert.Null(got.Value.SortIndex);
        Assert.Equal(got.Value.CreatedUtc, got.Value.UpdatedUtc);

        var listed = await files.ListAsync(wd);
        AssertOk(listed);
        var row = Assert.Single(listed.Value);
        Assert.Equal(created.Value, row.Id);
        Assert.Null(row.Content);
        Assert.Equal(5, row.ContentLength);
        Assert.Equal("Notes", row.Title);

        await Task.Delay(20);

        var updated = await files.UpdateAsync(created.Value, wd, title: " Renamed ", content: "world!");
        AssertVoidOk(updated);

        var after = await files.GetAsync(created.Value, wd);
        AssertOk(after);
        Assert.Equal("Renamed", after.Value.Title);
        Assert.Equal("world!", after.Value.Content);
        Assert.Equal(6, after.Value.ContentLength);
        Assert.Equal(got.Value.CreatedUtc, after.Value.CreatedUtc);
        Assert.True(after.Value.UpdatedUtc > got.Value.UpdatedUtc, "UpdateAsync must bump UpdatedUtc.");

        var deleted = await files.DeleteAsync(created.Value, wd);
        AssertVoidOk(deleted);

        var gone = await files.GetAsync(created.Value, wd);
        Assert.True(gone.IsError);
        Assert.Equal($"Scratch file '{created.Value}' not found.", gone.Error);

        var empty = await files.ListAsync(wd);
        AssertOk(empty);
        Assert.Empty(empty.Value);
    }

    [Fact]
    public async Task Get_OtherWorkDirectory_NotFound()
    {
        await using var fixture = await ScratchFixture.CreateAsync();
        var otherWd = await fixture.SeedWorkDirectoryAsync();

        var created = await fixture.Files.CreateAsync(fixture.WorkDirectoryId, "Scoped", "body");
        AssertOk(created);
        var id = created.Value;

        var get = await fixture.Files.GetAsync(id, otherWd);
        Assert.True(get.IsError, "GetAsync must not return a scratch file from another work directory.");
        Assert.Equal($"Scratch file '{id}' not found.", get.Error);

        var update = await fixture.Files.UpdateAsync(id, otherWd, title: "Hijacked", content: null);
        Assert.True(update.IsError);
        Assert.Equal($"Scratch file '{id}' not found.", update.Error);

        var delete = await fixture.Files.DeleteAsync(id, otherWd);
        Assert.True(delete.IsError);
        Assert.Equal($"Scratch file '{id}' not found.", delete.Error);

        var move = await fixture.Files.MoveAsync(id, otherWd, 0);
        Assert.True(move.IsError);
        Assert.Equal($"Scratch file '{id}' not found.", move.Error);

        var stillThere = await fixture.Files.GetAsync(id, fixture.WorkDirectoryId);
        AssertOk(stillThere);
        Assert.Equal("Scoped", stillThere.Value.Title);
        Assert.Equal("body", stillThere.Value.Content);
    }

    [Fact]
    public async Task Create_OverCap_Fails_NothingSaved()
    {
        await using var fixture = await ScratchFixture.CreateAsync();
        var wd = fixture.WorkDirectoryId;
        var over = new string('x', IDysonMetaScratchFileRepository.MaxContentChars + 1);

        var rejected = await fixture.Files.CreateAsync(wd, "Too big", over);
        Assert.True(rejected.IsError);
        Assert.Contains("131072", rejected.Error, StringComparison.Ordinal);
        Assert.Equal(
            $"Scratch file content is {over.Length} characters; the maximum is 131072 (128K). Nothing was saved.",
            rejected.Error);

        var list = await fixture.Files.ListAsync(wd);
        AssertOk(list);
        Assert.Empty(list.Value);

        var exact = new string('y', IDysonMetaScratchFileRepository.MaxContentChars);
        var ok = await fixture.Files.CreateAsync(wd, "Exact", exact);
        AssertOk(ok);

        var got = await fixture.Files.GetAsync(ok.Value, wd);
        AssertOk(got);
        Assert.Equal(IDysonMetaScratchFileRepository.MaxContentChars, got.Value.ContentLength);
        Assert.Equal(exact, got.Value.Content);
    }

    [Fact]
    public async Task Update_OverCap_Fails_ContentUnchanged()
    {
        await using var fixture = await ScratchFixture.CreateAsync();
        var wd = fixture.WorkDirectoryId;
        var created = await fixture.Files.CreateAsync(wd, "Keep", "original");
        AssertOk(created);

        var over = new string('z', IDysonMetaScratchFileRepository.MaxContentChars + 1);
        var rejected = await fixture.Files.UpdateAsync(created.Value, wd, title: null, content: over);
        Assert.True(rejected.IsError);
        Assert.Contains("131072", rejected.Error, StringComparison.Ordinal);

        var got = await fixture.Files.GetAsync(created.Value, wd);
        AssertOk(got);
        Assert.Equal("Keep", got.Value.Title);
        Assert.Equal("original", got.Value.Content);
    }

    [Fact]
    public async Task List_Sort_IndexedFirst_ThenUnindexedByUpdatedDesc()
    {
        await using var fixture = await ScratchFixture.CreateAsync();
        var wd = fixture.WorkDirectoryId;

        // Create order is id order. Stamp times and indexes directly so ties are deterministic.
        var olderNull = await fixture.Files.CreateAsync(wd, "Older null", "a");
        var newerNull = await fixture.Files.CreateAsync(wd, "Newer null", "b");
        var tieLow = await fixture.Files.CreateAsync(wd, "Tie low id", "c");
        var tieHigh = await fixture.Files.CreateAsync(wd, "Tie high id", "d");
        var indexOne = await fixture.Files.CreateAsync(wd, "Index 1", "e");
        var indexZero = await fixture.Files.CreateAsync(wd, "Index 0", "f");
        AssertOk(olderNull);
        AssertOk(newerNull);
        AssertOk(tieLow);
        AssertOk(tieHigh);
        AssertOk(indexOne);
        AssertOk(indexZero);

        var older = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var tie = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        await fixture.StampAsync(
            (olderNull.Value, null, older),
            (newerNull.Value, null, newer),
            (tieLow.Value, null, tie),
            (tieHigh.Value, null, tie),
            (indexOne.Value, 1, older),
            (indexZero.Value, 0, newer));

        var list = await fixture.Files.ListAsync(wd);
        AssertOk(list);
        Assert.Equal(
            new[]
            {
                indexZero.Value,
                indexOne.Value,
                newerNull.Value,
                tieHigh.Value,
                tieLow.Value,
                olderNull.Value,
            },
            list.Value.Select(x => x.Id).ToArray());
        Assert.All(list.Value, row => Assert.Null(row.Content));
    }

    [Fact]
    public async Task Move_RenumbersAllRowsDensely()
    {
        await using var fixture = await ScratchFixture.CreateAsync();
        var wd = fixture.WorkDirectoryId;
        var ids = new long[4];
        for (var i = 0; i < ids.Length; i++)
        {
            var created = await fixture.Files.CreateAsync(wd, $"Row {i}", $"c{i}");
            AssertOk(created);
            ids[i] = created.Value;
        }

        var before = await fixture.Files.ListAsync(wd);
        AssertOk(before);
        Assert.Equal(4, before.Value.Count);
        Assert.All(before.Value, row => Assert.Null(row.SortIndex));
        var stamps = before.Value.ToDictionary(x => x.Id, x => x.UpdatedUtc);

        // Unindexed rows sort by UpdatedUtc desc then Id desc, so the last created is first.
        // Move the displayed last (oldest id) to position 0.
        var moved = await fixture.Files.MoveAsync(ids[0], wd, 0);
        AssertVoidOk(moved);

        var after = await fixture.Files.ListAsync(wd);
        AssertOk(after);
        Assert.Equal(new[] { ids[0], ids[3], ids[2], ids[1] }, after.Value.Select(x => x.Id).ToArray());
        Assert.Equal(new int?[] { 0, 1, 2, 3 }, after.Value.Select(x => x.SortIndex).ToArray());
        foreach (var row in after.Value)
            Assert.Equal(stamps[row.Id], row.UpdatedUtc);
    }

    [Fact]
    public async Task Move_ClampsPosition_And_AfterDeleteGapsStillSort()
    {
        await using var fixture = await ScratchFixture.CreateAsync();
        var wd = fixture.WorkDirectoryId;
        var a = await fixture.Files.CreateAsync(wd, "A", "a");
        var b = await fixture.Files.CreateAsync(wd, "B", "b");
        var c = await fixture.Files.CreateAsync(wd, "C", "c");
        AssertOk(a);
        AssertOk(b);
        AssertOk(c);

        // Pin A, B, C in that order (create order is newest-first until moved).
        AssertVoidOk(await fixture.Files.MoveAsync(a.Value, wd, 0));
        AssertVoidOk(await fixture.Files.MoveAsync(c.Value, wd, 99));

        var pinned = await fixture.Files.ListAsync(wd);
        AssertOk(pinned);
        Assert.Equal(new[] { a.Value, b.Value, c.Value }, pinned.Value.Select(x => x.Id).ToArray());
        Assert.Equal(new int?[] { 0, 1, 2 }, pinned.Value.Select(x => x.SortIndex).ToArray());

        // Negative position clamps to 0.
        AssertVoidOk(await fixture.Files.MoveAsync(c.Value, wd, -5));
        var clamped = await fixture.Files.ListAsync(wd);
        AssertOk(clamped);
        Assert.Equal(new[] { c.Value, a.Value, b.Value }, clamped.Value.Select(x => x.Id).ToArray());
        Assert.Equal(new int?[] { 0, 1, 2 }, clamped.Value.Select(x => x.SortIndex).ToArray());

        AssertVoidOk(await fixture.Files.DeleteAsync(a.Value, wd));
        var gapped = await fixture.Files.ListAsync(wd);
        AssertOk(gapped);
        Assert.Equal(new[] { c.Value, b.Value }, gapped.Value.Select(x => x.Id).ToArray());
        Assert.Equal(new int?[] { 0, 2 }, gapped.Value.Select(x => x.SortIndex).ToArray());
    }

    [Fact]
    public async Task Validation_Errors()
    {
        await using var fixture = await ScratchFixture.CreateAsync();
        var wd = fixture.WorkDirectoryId;
        var created = await fixture.Files.CreateAsync(wd, "Ok", "");
        AssertOk(created);

        var badId = await fixture.Files.GetAsync(0, wd);
        Assert.True(badId.IsError);
        Assert.Equal("Scratch file id must be a positive integer.", badId.Error);

        var negative = await fixture.Files.DeleteAsync(-1, wd);
        Assert.True(negative.IsError);
        Assert.Equal("Scratch file id must be a positive integer.", negative.Error);

        var emptyWd = await fixture.Files.ListAsync(Guid.Empty);
        Assert.True(emptyWd.IsError);
        Assert.Equal("Work directory id is required.", emptyWd.Error);

        var createEmptyWd = await fixture.Files.CreateAsync(Guid.Empty, "T", "c");
        Assert.True(createEmptyWd.IsError);
        Assert.Equal("Work directory id is required.", createEmptyWd.Error);

        var blank = await fixture.Files.CreateAsync(wd, "   ", "c");
        Assert.True(blank.IsError);
        Assert.Equal("Title is required.", blank.Error);

        var nullTitle = await fixture.Files.UpdateAsync(created.Value, wd, title: null, content: null);
        Assert.True(nullTitle.IsError);
        Assert.Equal("Nothing to update.", nullTitle.Error);

        var nullContent = await fixture.Files.CreateAsync(wd, "T", null!);
        Assert.True(nullContent.IsError);
        Assert.Equal("Content is required.", nullContent.Error);

        var longTitle = await fixture.Files.CreateAsync(wd, new string('t', 201), "c");
        Assert.True(longTitle.IsError);
        Assert.Equal("Title must be at most 200 characters.", longTitle.Error);

        var missing = await fixture.Files.GetAsync(9999, wd);
        Assert.True(missing.IsError);
        Assert.Equal("Scratch file '9999' not found.", missing.Error);

        var unknownWd = Guid.NewGuid();
        var unknown = await fixture.Files.ListAsync(unknownWd);
        Assert.True(unknown.IsError);
        Assert.Equal($"Work directory '{unknownWd}' not found.", unknown.Error);

        var still = await fixture.Files.GetAsync(created.Value, wd);
        AssertOk(still);
        Assert.Equal("", still.Value.Content);
    }

    private static void AssertVoidOk(VoidResult<string> result)
    {
        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
    }

    private static void AssertOk<T>(Result<T, string> result)
    {
        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
    }

    private sealed class ScratchFixture : IAsyncDisposable
    {
        private ScratchFixture(
            DysonDbAccessor accessor,
            SqliteConnection connection,
            DysonMetaScratchFileRepository files,
            Guid workDirectoryId)
        {
            Accessor = accessor;
            Connection = connection;
            Files = files;
            WorkDirectoryId = workDirectoryId;
        }

        public DysonDbAccessor Accessor { get; }
        public SqliteConnection Connection { get; }
        public DysonMetaScratchFileRepository Files { get; }
        public Guid WorkDirectoryId { get; }

        public static async Task<ScratchFixture> CreateAsync()
        {
            var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
            var subject = DysonTempDb.Subject();
            var files = DysonTempDb.ScratchFiles(accessor, subject);
            var wd = await SeedWorkDirectoryAsync(accessor, subject.SubjectId);
            return new ScratchFixture(accessor, connection, files, wd);
        }

        public Task<Guid> SeedWorkDirectoryAsync() =>
            SeedWorkDirectoryAsync(Accessor, DysonSubjects.Local);

        public Task StampAsync(params (long Id, int? SortIndex, DateTime UpdatedUtc)[] stamps) =>
            Accessor.RunAsync(async (db, ct) =>
            {
                foreach (var stamp in stamps)
                {
                    var entity = await db.MetaScratchFiles
                        .FirstAsync(x => x.Id == stamp.Id, ct)
                        .ConfigureAwait(false);
                    entity.SortIndex = stamp.SortIndex;
                    entity.UpdatedUtc = stamp.UpdatedUtc;
                }

                await DysonDbAccessor.SaveChangesAsync(db, ct).ConfigureAwait(false);
            });

        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();

        private static async Task<Guid> SeedWorkDirectoryAsync(DysonDbAccessor accessor, string subjectId)
        {
            var id = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await accessor.RunAsync(async (db, ct) =>
            {
                db.WorkDirectories.Add(new DysonWorkDirectoryEntity
                {
                    Id = id,
                    SubjectId = subjectId,
                    Name = "scratch-test",
                    AbsolutePath = Path.Combine(Path.GetTempPath(), id.ToString("N")),
                    CreatedUtc = now,
                    LastOpenedUtc = now,
                });
                await DysonDbAccessor.SaveChangesAsync(db, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return id;
        }
    }
}
