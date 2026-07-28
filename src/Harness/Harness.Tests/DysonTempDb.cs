using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

/// <summary>Test helpers: factory + accessor over temp SQLite (memory or file).</summary>
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

    private sealed class DelegateDbContextFactory(DbContextOptions<DysonDbContext> options)
        : IDbContextFactory<DysonDbContext>
    {
        public DysonDbContext CreateDbContext() => new(options);
    }
}
