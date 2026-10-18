using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PgTtl.Internal;
using PgTtl.Options;
using Testcontainers.PostgreSql;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        DataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
            await DataSource.DisposeAsync();

        await _postgres.DisposeAsync();
    }
}

public sealed class ExpiredRowCleanerTests(PostgreSqlFixture fixture)
    : IClassFixture<PostgreSqlFixture>
{
    private readonly ExpiredRowCleaner _cleaner =
        new(NullLogger<ExpiredRowCleaner>.Instance);

    [Fact]
    public async Task RunOnceAsync_DeletesExpiredRowsAcrossSchemasAndTimestampTypes()
    {
        var schemaA = NewSchemaName();
        var schemaB = $"schema with space {Guid.NewGuid():N}";
        var qSchemaA = Quote(schemaA);
        var qSchemaB = Quote(schemaB);
        var qTableB = Quote("table\"with\"quotes");

        await ExecuteAsync($"CREATE SCHEMA {qSchemaA}; CREATE SCHEMA {qSchemaB};");
        await ExecuteAsync($"""
            CREATE TABLE {qSchemaA}.sessions (
                id integer PRIMARY KEY,
                ttl_expires_at timestamptz NULL
            );
            INSERT INTO {qSchemaA}.sessions VALUES
                (1, CURRENT_TIMESTAMP - INTERVAL '1 hour'),
                (2, NULL),
                (3, CURRENT_TIMESTAMP + INTERVAL '1 hour'),
                (4, CURRENT_TIMESTAMP - INTERVAL '1 second'),
                (5, CURRENT_TIMESTAMP);

            CREATE TABLE {qSchemaB}.{qTableB} (
                id integer PRIMARY KEY,
                ttl_expires_at timestamp without time zone NULL
            );
            INSERT INTO {qSchemaB}.{qTableB} VALUES
                (10, CURRENT_TIMESTAMP::timestamp - INTERVAL '1 hour'),
                (11, NULL),
                (12, CURRENT_TIMESTAMP::timestamp + INTERVAL '1 hour');

            CREATE TABLE {qSchemaA}.wrong_column_name (
                id integer PRIMARY KEY,
                expires_at timestamptz
            );
            INSERT INTO {qSchemaA}.wrong_column_name VALUES
                (20, CURRENT_TIMESTAMP - INTERVAL '1 hour');

            CREATE TABLE {qSchemaA}.wrong_column_type (
                id integer PRIMARY KEY,
                ttl_expires_at text
            );
            INSERT INTO {qSchemaA}.wrong_column_type VALUES
                (30, '2000-01-01 00:00:00+00');

            CREATE TABLE {qSchemaA}.delete_fails (
                id integer PRIMARY KEY,
                ttl_expires_at timestamptz
            );
            INSERT INTO {qSchemaA}.delete_fails VALUES
                (40, CURRENT_TIMESTAMP - INTERVAL '1 hour');
            CREATE FUNCTION {qSchemaA}.reject_delete() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'delete intentionally blocked for integration test';
                END;
                $$;
            CREATE TRIGGER reject_delete BEFORE DELETE ON {qSchemaA}.delete_fails
                FOR EACH ROW EXECUTE FUNCTION {qSchemaA}.reject_delete();

            CREATE VIEW {qSchemaA}.sessions_view AS
                SELECT id, ttl_expires_at FROM {qSchemaA}.sessions;
            """);

        try
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            var deleted = await _cleaner.RunOnceAsync(connection);

            Assert.Equal(4, deleted);
            Assert.Equal(new[] { 2, 3 }, await ReadIdsAsync(schemaA, "sessions"));
            Assert.Equal(new[] { 11, 12 }, await ReadIdsAsync(schemaB, "table\"with\"quotes"));
            Assert.Equal(new[] { 20 }, await ReadIdsAsync(schemaA, "wrong_column_name"));
            Assert.Equal(new[] { 30 }, await ReadIdsAsync(schemaA, "wrong_column_type"));
            Assert.Equal(new[] { 40 }, await ReadIdsAsync(schemaA, "delete_fails"));

            // Running cleanup again is safe and deletes nothing else.
            Assert.Equal(0, await _cleaner.RunOnceAsync(connection));
        }
        finally
        {
            await ExecuteAsync($"DROP SCHEMA {qSchemaA} CASCADE; DROP SCHEMA {qSchemaB} CASCADE;");
        }
    }

    [Fact]
    public async Task RunOnceAsync_ReturnsZeroWhenNoEligibleTablesExist()
    {
        var schema = NewSchemaName();
        var qSchema = Quote(schema);
        await ExecuteAsync($"""
            CREATE SCHEMA {qSchema};
            CREATE TABLE {qSchema}.ordinary_table (id integer PRIMARY KEY);
            CREATE TABLE {qSchema}.wrong_type_table (ttl_expires_at text);
            """);

        try
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            Assert.Equal(0, await _cleaner.RunOnceAsync(connection));
        }
        finally
        {
            await ExecuteAsync($"DROP SCHEMA {qSchema} CASCADE;");
        }
    }

    [Fact]
    public async Task RunOnceAsync_ObservesCancellation()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _cleaner.RunOnceAsync(connection, cancellation.Token));
    }

    [Fact]
    public async Task AdvisoryLock_AllowsOneHolderAndCanBeReleasedExplicitly()
    {
        await using var secondConnection = await fixture.DataSource.OpenConnectionAsync();

        await using (var firstConnection = await fixture.DataSource.OpenConnectionAsync())
        {
            Assert.True(await PgTtlAdvisoryLock.TryAcquireAsync(firstConnection));
            Assert.False(await PgTtlAdvisoryLock.TryAcquireAsync(secondConnection));
            Assert.True(await PgTtlAdvisoryLock.ReleaseAsync(firstConnection));
            Assert.False(await PgTtlAdvisoryLock.ReleaseAsync(firstConnection));
            Assert.True(await PgTtlAdvisoryLock.TryAcquireAsync(secondConnection));
            Assert.True(await PgTtlAdvisoryLock.ReleaseAsync(secondConnection));
        }
    }

    [Fact]
    public async Task HostedService_HoldsLockAndCleansRowsAddedAfterStartup()
    {
        var schema = NewSchemaName();
        var qSchema = Quote(schema);
        var table = $"{qSchema}.sessions";
        await ExecuteAsync($"""
            CREATE SCHEMA {qSchema};
            CREATE TABLE {table} (
                id integer PRIMARY KEY,
                ttl_expires_at timestamptz NULL
            );
            """);

        var options = new PgTtlOptions
        {
            ConnectionString = fixture.DataSource.ConnectionString,
            ScanInterval = TimeSpan.FromMilliseconds(75)
        };
        using var hostedService = new PgTtlHostedService(
            fixture.DataSource,
            _cleaner,
            options,
            NullLogger<PgTtlHostedService>.Instance);

        try
        {
            await hostedService.StartAsync(CancellationToken.None);

            // Wait until the service owns the session lock before inserting a row.
            await WaitUntilAsync(async () =>
            {
                await using var observer = await fixture.DataSource.OpenConnectionAsync();
                var acquired = await PgTtlAdvisoryLock.TryAcquireAsync(observer);
                if (acquired)
                    await PgTtlAdvisoryLock.ReleaseAsync(observer);
                return !acquired;
            });

            // The advisory lock must not prevent normal application connections from writing.
            await ExecuteAsync($"INSERT INTO {table} VALUES (1, CURRENT_TIMESTAMP - INTERVAL '1 minute');");

            await WaitUntilAsync(async () =>
            {
                await using var command = fixture.DataSource.CreateCommand(
                    $"SELECT count(*) FROM {table} WHERE id = 1");
                return (long)(await command.ExecuteScalarAsync())! == 0;
            });
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
            await ExecuteAsync($"DROP SCHEMA {qSchema} CASCADE;");
        }
    }

    private async Task<int[]> ReadIdsAsync(string schema, string table)
    {
        var sql = $"SELECT id FROM {Quote(schema)}.{Quote(table)} ORDER BY id";
        await using var command = fixture.DataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync();

        var ids = new List<int>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetInt32(0));

        return ids.ToArray();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = fixture.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(50);
        }

        throw new TimeoutException("Condition was not met before the timeout.");
    }

    private static string NewSchemaName() => $"schema_{Guid.NewGuid():N}";

    private static string Quote(string identifier) =>
        new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
}
