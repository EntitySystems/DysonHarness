using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

/// <summary>
/// Process-wide, thread-safe EF entrypoint: one gate per DB path, fresh context per
/// <see cref="RunAsync{T}"/>, busy/locked retry around the operation.
/// </summary>
public sealed class DysonDbAccessor
{
    private const int BusyRetryAttempts = 5;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IDbContextFactory<DysonDbContext> _factory;
    private readonly string _gateKey;

    public DysonDbAccessor(IDbContextFactory<DysonDbContext> factory, string databasePath)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _gateKey = NormalizeGateKey(databasePath);
    }

    public string DatabasePath => _gateKey;

    public Task<T> RunAsync<T>(
        Func<DysonDbContext, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunCoreAsync(work, cancellationToken);
    }

    public Task RunAsync(
        Func<DysonDbContext, CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunCoreAsync(
            async (db, ct) =>
            {
                await work(db, ct).ConfigureAwait(false);
                return 0;
            },
            cancellationToken);
    }

    /// <summary>Busy/locked retry around <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>.</summary>
    public static async Task<int> SaveChangesAsync(
        DysonDbContext db,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        Exception? lastBusy = null;
        for (var attempt = 0; attempt < BusyRetryAttempts; attempt++)
        {
            try
            {
                return await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < BusyRetryAttempts - 1 && IsSqliteBusyOrLocked(ex))
            {
                lastBusy = ex;
                await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastBusy ?? new InvalidOperationException("SQLite busy retry exhausted.");
    }

    internal static bool IsSqliteBusyOrLocked(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SqliteException se && se.SqliteErrorCode is 5 or 6)
                return true;
        }

        return false;
    }

    internal static bool IsEfConcurrentContext(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is InvalidOperationException &&
                e.Message.Contains("second operation", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsContention(Exception ex) =>
        IsSqliteBusyOrLocked(ex) || IsEfConcurrentContext(ex);

    private async Task<T> RunCoreAsync<T>(
        Func<DysonDbContext, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(_gateKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            return await work(db, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(20 * (attempt + 1));

    private static string NormalizeGateKey(string databasePath)
    {
        try
        {
            return Path.GetFullPath(databasePath);
        }
        catch (Exception)
        {
            return databasePath;
        }
    }
}
