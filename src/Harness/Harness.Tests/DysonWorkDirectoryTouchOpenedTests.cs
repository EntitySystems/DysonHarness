using DysonHarness;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harness.Tests;

/// <summary>
/// ponytail: work-directory writes must return an error Result on SQLITE_BUSY/LOCKED, not throw.
/// Uses a 1s busy timeout so lock contention cannot stall 30s × 5 retries.
/// </summary>
public class DysonWorkDirectoryTouchOpenedTests
{
    [Fact]
    public async Task UpdateGitMetadataAsync_busy_database_returns_error_result()
    {
        var (accessor, path) = OpenShortTimeoutFileAccessor();
        var workRoot = Path.Combine(Path.GetTempPath(), $"dyson-wd-gitmeta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);

        try
        {
            var workdirs = DysonTempDb.WorkDirectories(accessor);
            var created = await workdirs.CreateAsync(workRoot, "gitmeta-busy");
            if (created.IsError)
                throw new InvalidOperationException(created.Error);

            var id = created.Value;
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                DefaultTimeout = 1,
                Pooling = false,
            }.ConnectionString;

            await using var locker = new SqliteConnection(cs);
            await locker.OpenAsync();
            await using (var begin = locker.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE;";
                await begin.ExecuteNonQueryAsync();
            }

            try
            {
                VoidResult<string> result;
                try
                {
                    result = await workdirs.UpdateGitMetadataAsync(id, "https://example.com/repo.git", "other")
                        .WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "UpdateGitMetadataAsync must return an error Result on SQLITE_BUSY/LOCKED, not throw.",
                        ex);
                }

                Assert.True(result.IsError, "Expected error Result when the write lock is held.");
                Assert.Contains("busy", result.Error, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await using var rollback = locker.CreateCommand();
                rollback.CommandText = "ROLLBACK;";
                try
                {
                    await rollback.ExecuteNonQueryAsync();
                }
                catch (SqliteException)
                {
                    // connection already closed / txn ended
                }
            }
        }
        finally
        {
            TryDelete(workRoot);
            TryDeleteDb(path);
        }
    }

    [Fact]
    public async Task TouchOpenedAsync_busy_database_returns_error_result()
    {
        var (accessor, path) = OpenShortTimeoutFileAccessor();
        var workRoot = Path.Combine(Path.GetTempPath(), $"dyson-wd-touch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);

        try
        {
            var workdirs = DysonTempDb.WorkDirectories(accessor);
            var created = await workdirs.CreateAsync(workRoot, "touch-busy");
            if (created.IsError)
                throw new InvalidOperationException(created.Error);

            var id = created.Value;
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                DefaultTimeout = 1,
                Pooling = false,
            }.ConnectionString;

            await using var locker = new SqliteConnection(cs);
            await locker.OpenAsync();
            await using (var begin = locker.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE;";
                await begin.ExecuteNonQueryAsync();
            }

            try
            {
                VoidResult<string> result;
                try
                {
                    result = await workdirs.TouchOpenedAsync(id)
                        .WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "TouchOpenedAsync must return an error Result on SQLITE_BUSY/LOCKED, not throw.",
                        ex);
                }

                Assert.True(result.IsError, "Expected error Result when the write lock is held.");
                Assert.Contains("busy", result.Error, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await using var rollback = locker.CreateCommand();
                rollback.CommandText = "ROLLBACK;";
                try
                {
                    await rollback.ExecuteNonQueryAsync();
                }
                catch (SqliteException)
                {
                    // connection already closed / txn ended
                }
            }
        }
        finally
        {
            TryDelete(workRoot);
            TryDeleteDb(path);
        }
    }

    private static (DysonDbAccessor Accessor, string Path) OpenShortTimeoutFileAccessor()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dyson-test-{Guid.NewGuid():N}.db");
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            DefaultTimeout = 1,
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<DysonDbContext>()
            .UseSqlite(cs, sqlite => sqlite.CommandTimeout(2))
            .Options;

        using (var db = new DysonDbContext(options))
            db.Database.Migrate();

        var factory = new DelegateDbContextFactory(options);
        return (new DysonDbAccessor(factory, path), path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static void TryDeleteDb(string path)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private sealed class DelegateDbContextFactory(DbContextOptions<DysonDbContext> options)
        : IDbContextFactory<DysonDbContext>
    {
        public DysonDbContext CreateDbContext() => new(options);
    }
}
