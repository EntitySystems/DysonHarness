using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public enum DysonSqliteVacuumOutcome
{
    Skipped,
    Compacted,
}

internal static class DysonSqliteVacuum
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    // ponytail: skip tiny freelists so we don't rewrite hundreds of MB for one page;
    // raise/lower this constant if ops want more/less aggression.
    internal const int MinFreelistPages = 64;

    internal const int CommandTimeoutSeconds = 300;

    internal static async Task<Result<DysonSqliteVacuumOutcome, string>> TryRunAsync(
        DysonDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        try
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
                await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText = "PRAGMA freelist_count;";
            var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var freelistCount = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
            if (freelistCount < MinFreelistPages)
                return Result<DysonSqliteVacuumOutcome, string>.AsValue(DysonSqliteVacuumOutcome.Skipped);

            command.CommandText = "VACUUM;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return Result<DysonSqliteVacuumOutcome, string>.AsValue(DysonSqliteVacuumOutcome.Compacted);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<DysonSqliteVacuumOutcome, string>.AsError($"SQLite busy or locked: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<DysonSqliteVacuumOutcome, string>.AsError($"VACUUM failed: {ex.Message}");
        }
    }
}
