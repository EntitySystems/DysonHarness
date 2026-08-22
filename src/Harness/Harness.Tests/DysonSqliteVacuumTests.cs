using System.Globalization;
using DysonHarness;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Tests;

/// <summary>
/// ponytail: file-backed vacuum skip/compact + hosted cancel during the 10-minute delay.
/// Never open live %LocalAppData% DysonDev/DysonProd dyson.db.
/// </summary>
public class DysonSqliteVacuumTests
{
    [Fact]
    public async Task Fresh_Migrated_File_Skips_Vacuum()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var result = await accessor.RunAsync(
                (db, ct) => DysonSqliteVacuum.TryRunAsync(db, ct));
            if (result.IsError)
                throw new InvalidOperationException(result.Error);
            if (result.Value != DysonSqliteVacuumOutcome.Skipped)
                throw new InvalidOperationException($"Expected Skipped, got {result.Value}.");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Delete_Large_Setting_Then_Vacuum_Compacts()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var settings = DysonTempDb.Settings(accessor);
            var payloadChars = 2 * 1024 * 1024;
            var freelist = 0L;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var set = await settings.SetSettingAsync("vacuum-blob", new string('x', payloadChars));
                if (set.IsError)
                    throw new InvalidOperationException(set.Error);

                var deleted = await settings.SetSettingAsync("vacuum-blob", null);
                if (deleted.IsError)
                    throw new InvalidOperationException(deleted.Error);

                // WAL can hide deleted pages until checkpoint; product threshold stays 64.
                await accessor.RunAsync(CheckpointWalAsync);
                freelist = await accessor.RunAsync(ReadFreelistCountAsync);
                if (freelist >= DysonSqliteVacuum.MinFreelistPages)
                    break;

                payloadChars *= 2;
            }

            if (freelist < DysonSqliteVacuum.MinFreelistPages)
            {
                throw new InvalidOperationException(
                    $"freelist_count was {freelist} after delete; need >= {DysonSqliteVacuum.MinFreelistPages}. " +
                    "Increase payload in this test — do not lower MinFreelistPages.");
            }

            var beforePages = await accessor.RunAsync(ReadPageCountAsync);
            var beforeLength = new FileInfo(path).Length;

            // Same open connection as the checkpointed freelist, so WAL cannot hide pages.
            var result = await accessor.RunAsync(async (db, ct) =>
            {
                await CheckpointWalAsync(db, ct);
                return await DysonSqliteVacuum.TryRunAsync(db, ct);
            });
            if (result.IsError)
                throw new InvalidOperationException(result.Error);
            if (result.Value != DysonSqliteVacuumOutcome.Compacted)
            {
                throw new InvalidOperationException(
                    $"Expected Compacted, got {result.Value} (freelist before={freelist}).");
            }

            SqliteConnection.ClearAllPools();

            var afterFreelist = await accessor.RunAsync(ReadFreelistCountAsync);
            if (afterFreelist > 8)
            {
                throw new InvalidOperationException(
                    $"Expected freelist near 0 after vacuum, got {afterFreelist}.");
            }

            var afterPages = await accessor.RunAsync(ReadPageCountAsync);
            SqliteConnection.ClearAllPools();
            var afterLength = new FileInfo(path).Length;
            if (afterPages >= beforePages && afterLength >= beforeLength)
            {
                throw new InvalidOperationException(
                    $"Vacuum did not shrink the main db: pages {beforePages} -> {afterPages}, " +
                    $"file {beforeLength} -> {afterLength}.");
            }
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task HostedService_StopAsync_During_Initial_Delay_Returns_Promptly()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var service = new DysonSqliteVacuumHostedService(
                accessor,
                NullLogger<DysonSqliteVacuumHostedService>.Instance);
            await service.StartAsync(CancellationToken.None);
            try
            {
                await service.StopAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                service.Dispose();
            }
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static async Task CheckpointWalAsync(DysonDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ReadFreelistCountAsync(
        DysonDbContext db,
        CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA freelist_count;";
        var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<long> ReadPageCountAsync(
        DysonDbContext db,
        CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA page_count;";
        var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
