using DysonHarness;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harness.Tests;

/// <summary>
/// ponytail: auto-increment planId, work-directory scoping, kind-shape invariant,
/// filtered unique index, ListAsync omits Markdown, cascade delete.
/// </summary>
public class DysonPlanRepositoryTests
{
    [Fact]
    public async Task CreateAsync_returns_nonzero_increasing_ids()
    {
        await using var fixture = await PlanFixture.CreateAsync();
        var plans = fixture.Plans;
        var wd = fixture.WorkDirectoryId;

        var first = await plans.CreateAsync(wd, DysonPlanKind.MetaPlan, "One", "# one", planRelativePath: null);
        AssertOk(first);
        Assert.True(first.Value > 0, $"Expected non-zero planId, got {first.Value}.");

        var second = await plans.CreateAsync(wd, DysonPlanKind.MetaPlan, "Two", "# two", planRelativePath: null);
        AssertOk(second);
        Assert.True(second.Value > first.Value, $"Expected increasing ids, got {first.Value} then {second.Value}.");
    }

    [Fact]
    public async Task AddPlans_migration_applies_and_assigns_ids()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var subject = DysonTempDb.Subject();
            var plans = new DysonPlanRepository(accessor, subject);
            var wd = await PlanFixture.SeedWorkDirectoryAsync(accessor, subject.SubjectId);

            var first = await plans.CreateAsync(wd, DysonPlanKind.MetaPlan, "Mig1", "# one", null);
            var second = await plans.CreateAsync(wd, DysonPlanKind.MetaPlan, "Mig2", "# two", null);
            AssertOk(first);
            AssertOk(second);
            Assert.True(first.Value > 0, $"Expected non-zero planId after Migrate(), got {first.Value}.");
            Assert.True(second.Value > first.Value, $"Expected increasing ids after Migrate(), got {first.Value} then {second.Value}.");
        }
        finally
        {
            TryDeleteDb(path);
        }
    }

    [Fact]
    public async Task Get_Update_Delete_do_not_cross_work_directory_boundary()
    {
        await using var fixture = await PlanFixture.CreateAsync();
        var otherWd = await fixture.SeedWorkDirectoryAsync();

        var created = await fixture.Plans.CreateAsync(
            fixture.WorkDirectoryId,
            DysonPlanKind.MetaPlan,
            "Scoped",
            "# body",
            planRelativePath: null);
        AssertOk(created);
        var planId = created.Value;

        var get = await fixture.Plans.GetAsync(planId, otherWd);
        Assert.True(get.IsError, "GetAsync must not return a plan from another work directory.");
        Assert.Contains("not found", get.Error, StringComparison.OrdinalIgnoreCase);

        var update = await fixture.Plans.UpdateAsync(planId, otherWd, title: "Hijacked");
        Assert.True(update.IsError, "UpdateAsync must not patch a plan from another work directory.");
        Assert.Contains("not found", update.Error, StringComparison.OrdinalIgnoreCase);

        var delete = await fixture.Plans.DeleteAsync(planId, otherWd);
        Assert.True(delete.IsError, "DeleteAsync must not delete a plan from another work directory.");
        Assert.Contains("not found", delete.Error, StringComparison.OrdinalIgnoreCase);

        var stillThere = await fixture.Plans.GetAsync(planId, fixture.WorkDirectoryId);
        AssertOk(stillThere);
        Assert.Equal("Scoped", stillThere.Value.Title);
        Assert.Equal("# body", stillThere.Value.Markdown);
    }

    [Fact]
    public async Task MetaPlan_rejects_path_and_empty_markdown_ClassicPlan_rejects_null_path()
    {
        await using var fixture = await PlanFixture.CreateAsync();
        var wd = fixture.WorkDirectoryId;
        var plans = fixture.Plans;

        var withPath = await plans.CreateAsync(
            wd,
            DysonPlanKind.MetaPlan,
            "Meta with path",
            "# body",
            planRelativePath: ".dyson/plans/foo.md");
        Assert.True(withPath.IsError, "MetaPlan with a PlanRelativePath must be rejected.");
        Assert.Contains("PlanRelativePath", withPath.Error, StringComparison.OrdinalIgnoreCase);

        var emptyMd = await plans.CreateAsync(
            wd,
            DysonPlanKind.MetaPlan,
            "Meta empty",
            "",
            planRelativePath: null);
        Assert.True(emptyMd.IsError, "MetaPlan with empty Markdown must be rejected.");
        Assert.Contains("Markdown", emptyMd.Error, StringComparison.OrdinalIgnoreCase);

        var whitespaceMd = await plans.CreateAsync(
            wd,
            DysonPlanKind.MetaPlan,
            "Meta whitespace",
            "   ",
            planRelativePath: null);
        Assert.True(whitespaceMd.IsError, "MetaPlan with whitespace Markdown must be rejected.");

        var classicNoPath = await plans.CreateAsync(
            wd,
            DysonPlanKind.ClassicPlan,
            "Classic no path",
            markdown: null,
            planRelativePath: null);
        Assert.True(classicNoPath.IsError, "ClassicPlan with a null path must be rejected.");
        Assert.Contains("PlanRelativePath", classicNoPath.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Two_MetaPlan_rows_with_null_path_coexist_in_one_work_directory()
    {
        await using var fixture = await PlanFixture.CreateAsync();
        var wd = fixture.WorkDirectoryId;

        var a = await fixture.Plans.CreateAsync(wd, DysonPlanKind.MetaPlan, "A", "# a", planRelativePath: null);
        var b = await fixture.Plans.CreateAsync(wd, DysonPlanKind.MetaPlan, "B", "# b", planRelativePath: null);
        AssertOk(a);
        AssertOk(b);
        Assert.NotEqual(a.Value, b.Value);

        var list = await fixture.Plans.ListAsync(wd);
        AssertOk(list);
        Assert.Equal(2, list.Value.Count);
    }

    [Fact]
    public async Task ListAsync_omits_Markdown_GetAsync_returns_it()
    {
        await using var fixture = await PlanFixture.CreateAsync();
        var wd = fixture.WorkDirectoryId;
        const string body = "# hello from the body";

        var created = await fixture.Plans.CreateAsync(
            wd,
            DysonPlanKind.MetaPlan,
            "Listed",
            body,
            planRelativePath: null);
        AssertOk(created);

        var listed = await fixture.Plans.ListAsync(wd);
        AssertOk(listed);
        var row = Assert.Single(listed.Value);
        Assert.Null(row.Markdown);
        Assert.Equal("Listed", row.Title);
        Assert.Equal(created.Value, row.Id);
        Assert.Equal(DysonPlanStatus.Draft, row.Status);

        var got = await fixture.Plans.GetAsync(created.Value, wd);
        AssertOk(got);
        Assert.Equal(body, got.Value.Markdown);
    }

    [Fact]
    public async Task Deleting_work_directory_cascades_plan_rows()
    {
        await using var fixture = await PlanFixture.CreateAsync();
        var created = await fixture.Plans.CreateAsync(
            fixture.WorkDirectoryId,
            DysonPlanKind.MetaPlan,
            "Doomed",
            "# gone",
            planRelativePath: null);
        AssertOk(created);

        var deleted = await fixture.WorkDirectories.DeleteAsync(fixture.WorkDirectoryId);
        Assert.True(deleted.IsSuccess, deleted.IsError ? deleted.Error : null);

        var remaining = await fixture.CountPlansAsync(created.Value);
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task UpdateAsync_patches_and_bumps_UpdatedUtc()
    {
        await using var fixture = await PlanFixture.CreateAsync();
        var wd = fixture.WorkDirectoryId;
        var created = await fixture.Plans.CreateAsync(
            wd,
            DysonPlanKind.MetaPlan,
            "Draft title",
            "# v1",
            planRelativePath: null,
            note: "n1");
        AssertOk(created);

        var before = await fixture.Plans.GetAsync(created.Value, wd);
        AssertOk(before);

        await Task.Delay(20);

        var agentId = Guid.NewGuid();
        var updated = await fixture.Plans.UpdateAsync(
            created.Value,
            wd,
            title: "Built title",
            markdown: "# v2",
            status: DysonPlanStatus.Building,
            note: "n2",
            buildAgentId: agentId);
        Assert.True(updated.IsSuccess, updated.IsError ? updated.Error : null);

        var after = await fixture.Plans.GetAsync(created.Value, wd);
        AssertOk(after);
        Assert.Equal("Built title", after.Value.Title);
        Assert.Equal("# v2", after.Value.Markdown);
        Assert.Equal(DysonPlanStatus.Building, after.Value.Status);
        Assert.Equal("n2", after.Value.Note);
        Assert.Equal(agentId, after.Value.BuildAgentId);
        Assert.True(
            after.Value.UpdatedUtc > before.Value.UpdatedUtc,
            "UpdateAsync must bump UpdatedUtc.");
    }

    [Fact]
    public async Task ListAsync_returns_newest_first()
    {
        await using var fixture = await PlanFixture.CreateAsync();
        var wd = fixture.WorkDirectoryId;
        var older = await fixture.Plans.CreateAsync(wd, DysonPlanKind.MetaPlan, "Older", "# o", null);
        var newer = await fixture.Plans.CreateAsync(wd, DysonPlanKind.MetaPlan, "Newer", "# n", null);
        AssertOk(older);
        AssertOk(newer);

        var list = await fixture.Plans.ListAsync(wd);
        AssertOk(list);
        Assert.Equal(2, list.Value.Count);
        Assert.Equal(newer.Value, list.Value[0].Id);
        Assert.Equal(older.Value, list.Value[1].Id);
    }

    private static void AssertOk<T>(Result<T, string> result)
    {
        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
    }

    private static void TryDeleteDb(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var extra = path + suffix;
                if (File.Exists(extra))
                    File.Delete(extra);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private sealed class PlanFixture : IAsyncDisposable
    {
        private PlanFixture(
            DysonDbAccessor accessor,
            SqliteConnection connection,
            DysonPlanRepository plans,
            DysonWorkDirectoryRepository workDirectories,
            Guid workDirectoryId)
        {
            Accessor = accessor;
            Connection = connection;
            Plans = plans;
            WorkDirectories = workDirectories;
            WorkDirectoryId = workDirectoryId;
        }

        public DysonDbAccessor Accessor { get; }
        public SqliteConnection Connection { get; }
        public DysonPlanRepository Plans { get; }
        public DysonWorkDirectoryRepository WorkDirectories { get; }
        public Guid WorkDirectoryId { get; }

        public static async Task<PlanFixture> CreateAsync()
        {
            var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
            var subject = DysonTempDb.Subject();
            var plans = new DysonPlanRepository(accessor, subject);
            var workDirectories = DysonTempDb.WorkDirectories(accessor, subject);
            var wd = await SeedWorkDirectoryAsync(accessor, subject.SubjectId);
            return new PlanFixture(accessor, connection, plans, workDirectories, wd);
        }

        public Task<Guid> SeedWorkDirectoryAsync() =>
            SeedWorkDirectoryAsync(Accessor, DysonSubjects.Local);

        public Task<int> CountPlansAsync(long planId) =>
            Accessor.RunAsync(async (db, ct) =>
                await db.Plans.CountAsync(p => p.Id == planId, ct).ConfigureAwait(false));

        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();

        internal static async Task<Guid> SeedWorkDirectoryAsync(DysonDbAccessor accessor, string subjectId)
        {
            var id = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await accessor.RunAsync(async (db, ct) =>
            {
                db.WorkDirectories.Add(new DysonWorkDirectoryEntity
                {
                    Id = id,
                    SubjectId = subjectId,
                    Name = "plans-test",
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
