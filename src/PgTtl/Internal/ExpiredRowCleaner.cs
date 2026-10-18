using Microsoft.Extensions.Logging;
using Npgsql;

namespace PgTtl.Internal;

internal sealed class ExpiredRowCleaner(ILogger<ExpiredRowCleaner> logger)
{
    private readonly TableDiscovery _tableDiscovery = new();

    public async Task<int> RunOnceAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        var tables = await _tableDiscovery.DiscoverTablesAsync(
            connection, cancellationToken);

        var totalDeleted = 0;
        foreach (var table in tables)
        {
            try
            {
                totalDeleted += await DeleteExpiredRowsAsync(
                    connection, table, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Failed to delete expired rows from {Schema}.{Table}.",
                    table.Schema,
                    table.Name);
            }
        }

        return totalDeleted;
    }

    private static async Task<int> DeleteExpiredRowsAsync(
        NpgsqlConnection connection,
        TtlTable table,
        CancellationToken cancellationToken)
    {
        var identifierQuoter = new NpgsqlCommandBuilder();
        var schema = identifierQuoter.QuoteIdentifier(table.Schema);
        var name = identifierQuoter.QuoteIdentifier(table.Name);
        var ttlColumn = identifierQuoter.QuoteIdentifier("ttl_expires_at");

        var sql = $"""
            DELETE FROM {schema}.{name}
            WHERE {ttlColumn} IS NOT NULL
              AND {ttlColumn} <= CURRENT_TIMESTAMP;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
