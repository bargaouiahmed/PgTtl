using Npgsql;

namespace PgTtl.Internal;

internal sealed record TtlTable(string Schema, string Name);

internal sealed class TableDiscovery
{
    public async Task<List<TtlTable>> DiscoverTablesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT c.table_schema, c.table_name
            FROM information_schema.columns AS c
            JOIN information_schema.tables AS t
              ON c.table_schema = t.table_schema
             AND c.table_name = t.table_name
            WHERE c.column_name = 'ttl_expires_at'
              AND c.data_type IN (
                  'timestamp without time zone',
                  'timestamp with time zone'
              )
              AND t.table_type = 'BASE TABLE'
              AND c.table_schema NOT IN ('pg_catalog', 'information_schema');
            """;

        var tables = new List<TtlTable>();
        await using var command = new NpgsqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(new TtlTable(reader.GetString(0), reader.GetString(1)));
        }

        return tables;
    }
}
