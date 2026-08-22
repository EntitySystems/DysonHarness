using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DysonHarness;

public sealed class DysonSqliteVacuumHostedService(
    DysonDbAccessor accessor,
    ILogger<DysonSqliteVacuumHostedService> logger) : BackgroundService
{
    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private readonly ILogger<DysonSqliteVacuumHostedService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(DysonSqliteVacuum.Interval, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var started = Stopwatch.StartNew();
                    var result = await _accessor
                        .RunAsync(DysonSqliteVacuum.TryRunAsync, stoppingToken)
                        .ConfigureAwait(false);

                    if (result.IsError)
                    {
                        _logger.LogWarning("SQLite vacuum failed: {Error}", result.Error);
                    }
                    else if (result.Value == DysonSqliteVacuumOutcome.Compacted)
                    {
                        _logger.LogInformation(
                            "SQLite vacuum compacted the database in {Duration}.",
                            started.Elapsed);
                    }
                    else
                    {
                        _logger.LogInformation("SQLite vacuum skipped (freelist below threshold).");
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SQLite vacuum loop failed unexpectedly.");
                }

                await Task.Delay(DysonSqliteVacuum.Interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
