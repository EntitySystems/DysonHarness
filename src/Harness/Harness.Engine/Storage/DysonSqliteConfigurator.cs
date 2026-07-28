using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DysonHarness;

/// <summary>
/// Shared SQLite connection setup: busy timeout, WAL, and synchronous=NORMAL.
/// </summary>
public static class DysonSqliteConfigurator
{
    public const int DefaultTimeoutSeconds = 30;

    public static string BuildConnectionString(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            DefaultTimeout = DefaultTimeoutSeconds,
        }.ConnectionString;
    }

    public static void Configure(DbContextOptionsBuilder options, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.UseSqlite(BuildConnectionString(databasePath));
        options.AddInterceptors(SqlitePragmaConnectionInterceptor.Instance);
    }
}

/// <summary>Applies WAL + synchronous pragmas whenever a connection opens.</summary>
file sealed class SqlitePragmaConnectionInterceptor : DbConnectionInterceptor
{
    public static readonly SqlitePragmaConnectionInterceptor Instance = new();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
    }

    private static async Task ApplyPragmasAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var cmd = connection.CreateCommand();
        try
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            cmd.CommandText = "PRAGMA synchronous=NORMAL;";
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await cmd.DisposeAsync().ConfigureAwait(false);
        }
    }
}
