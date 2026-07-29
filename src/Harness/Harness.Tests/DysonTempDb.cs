using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

/// <summary>
/// Test helpers: factory + accessor over temp SQLite (memory or file), plus LocalDb repos
/// bound to a mutable subject context.
/// </summary>
internal static class DysonTempDb
{
    public static DysonDbAccessor OpenMemoryAccessor(out SqliteConnection connection)
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<DysonDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var db = new DysonDbContext(options))
            db.Database.EnsureCreated();

        var factory = new DelegateDbContextFactory(options);
        return new DysonDbAccessor(factory, $"memory:{Guid.NewGuid():N}");
    }

    public static (DysonDbAccessor Accessor, string Path) OpenFileAccessor()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"dyson-test-{Guid.NewGuid():N}.db");
        var optionsBuilder = new DbContextOptionsBuilder<DysonDbContext>();
        DysonSqliteConfigurator.Configure(optionsBuilder, path);
        var options = optionsBuilder.Options;

        using (var db = new DysonDbContext(options))
            db.Database.Migrate();

        var factory = new DelegateDbContextFactory(options);
        return (new DysonDbAccessor(factory, path), path);
    }

    /// <summary>Mutable subject for isolation tests (repos read SubjectId per call).</summary>
    public sealed class MutableSubjectContext(string subjectId) : IDysonSubjectContext
    {
        public string SubjectId { get; set; } = subjectId;
    }

    /// <summary>Access evaluator that denies every permission (ManageSharedProviders denial tests).</summary>
    public sealed class DenyingAccessEvaluator : IDysonAccessEvaluator
    {
        public IReadOnlyList<DysonRole> Roles { get; } = [DysonRole.Member];

        public bool Can(DysonPermission permission) => false;
    }

    public static MutableSubjectContext Subject(string subjectId = DysonSubjects.Local) =>
        new(subjectId);

    public static DysonSessionRepository Sessions(
        DysonDbAccessor accessor,
        IDysonSubjectContext? subject = null) =>
        new(accessor, subject ?? DysonFixedLocalSubjectContext.Instance);

    public static DysonModelRepository Models(
        DysonDbAccessor accessor,
        IDysonSubjectContext? subject = null,
        IDysonAccessEvaluator? access = null) =>
        new(
            accessor,
            subject ?? DysonFixedLocalSubjectContext.Instance,
            access ?? new DysonPermissiveAccessEvaluator());

    public static DysonConfiguredShellRepository Shells(
        DysonDbAccessor accessor,
        IDysonSubjectContext? subject = null) =>
        new(accessor, subject ?? DysonFixedLocalSubjectContext.Instance);

    public static DysonSubjectSettingsRepository Settings(
        DysonDbAccessor accessor,
        IDysonSubjectContext? subject = null) =>
        new(accessor, subject ?? DysonFixedLocalSubjectContext.Instance);

    public static DysonWorkDirectoryRepository WorkDirectories(
        DysonDbAccessor accessor,
        IDysonSubjectContext? subject = null) =>
        new(accessor, subject ?? DysonFixedLocalSubjectContext.Instance);

    private sealed class DelegateDbContextFactory(DbContextOptions<DysonDbContext> options)
        : IDbContextFactory<DysonDbContext>
    {
        public DysonDbContext CreateDbContext() => new(options);
    }
}
