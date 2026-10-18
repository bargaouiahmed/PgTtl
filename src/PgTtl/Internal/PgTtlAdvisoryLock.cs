using Npgsql;

namespace PgTtl.Internal;

internal static class PgTtlAdvisoryLock
{
    // The database name makes instances for the same database coordinate while
    // allowing PgTtl to run independently against other databases on the server.
    private const string TryAcquireSql = """
        SELECT pg_try_advisory_lock(
            hashtextextended('pgttl:' || current_database(), 0)
        );
        """;

    private const string ReleaseSql = """
        SELECT pg_advisory_unlock(
            hashtextextended('pgttl:' || current_database(), 0)
        );
        """;

    public static async Task<bool> TryAcquireAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(TryAcquireSql, connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public static async Task<bool> ReleaseAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(ReleaseSql, connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
