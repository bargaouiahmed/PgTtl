using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using PgTtl.Options;

namespace PgTtl.Internal;

internal sealed class PgTtlHostedService(
    NpgsqlDataSource dataSource,
    ExpiredRowCleaner expiredRowCleaner,
    PgTtlOptions options,
    ILogger<PgTtlHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection =
                    await dataSource.OpenConnectionAsync(stoppingToken);

                // Let the fast lock query finish so the service knows whether it must release it.
                if (!await PgTtlAdvisoryLock.TryAcquireAsync(connection, CancellationToken.None))
                {
                    logger.LogInformation(
                        "Another PgTtl instance holds the cleanup lock. Retrying after {Interval}.",
                        options.ScanInterval);
                    await Task.Delay(options.ScanInterval, stoppingToken);
                    continue;
                }

                try
                {
                    logger.LogInformation("This PgTtl instance acquired the cleanup lock.");

                    // Keep this connection open while holding the session-level advisory lock.
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        var rowsDeleted = await expiredRowCleaner.RunOnceAsync(
                            connection, stoppingToken);

                        if (rowsDeleted > 0)
                        {
                            logger.LogInformation(
                                "Deleted {RowsDeleted} expired rows.", rowsDeleted);
                        }

                        await Task.Delay(options.ScanInterval, stoppingToken);
                    }
                }
                finally
                {
                    // Closing an Npgsql connection usually returns its physical session
                    // to the pool, so explicitly release the session-level lock first.
                    if (connection.State == System.Data.ConnectionState.Open)
                    {
                        await PgTtlAdvisoryLock.ReleaseAsync(connection);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Leaving this scope closes the lock connection, allowing another instance
                // to acquire the lock while this instance waits to reconnect.
                logger.LogError(ex, "PgTtl cleanup failed. It will reconnect and retry.");

                try
                {
                    await Task.Delay(options.ScanInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
