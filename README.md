# PgTtl

PgTtl runs expired-row cleanup inside a .NET application that uses PostgreSQL. It scans tables the configured database user can see and deletes rows when a `ttl_expires_at` value is not null and is earlier than or equal to PostgreSQL's current time.

## Requirements

- .NET 10
- PostgreSQL 10 or later
- The database user must be able to see the relevant tables and delete rows from them.

The package currently targets .NET 10. It recognizes columns declared as `timestamp without time zone` or `timestamp with time zone`. `timestamp with time zone` (`timestamptz`) is recommended for expiry values.

## Install

After the package is published, install it in an application with:

```shell
dotnet add package PgTtl
```

## Configure

Register PgTtl with the application's services. The hosted cleanup starts when the application host starts.

```csharp
using PgTtl.DependencyInjection;

builder.Services.AddPgTtl(options =>
{
    options.ConnectionString =
        builder.Configuration.GetConnectionString("Default")!;
    options.ScanInterval = TimeSpan.FromMinutes(5);
});
```

The cleanup runs once on startup and repeats after each scan interval. The default interval is five minutes.

## Column behavior

Any base table in a visible schema is eligible when it has a `ttl_expires_at` column of type `timestamp` or `timestamptz`. The column may contain null values; null values are retained. Views and columns with other types are skipped.

For example:

```sql
CREATE TABLE public.sessions (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ttl_expires_at timestamptz NULL
);
```

PgTtl deletes rows using this condition:

```sql
ttl_expires_at IS NOT NULL AND ttl_expires_at <= CURRENT_TIMESTAMP
```

For `timestamp without time zone`, PostgreSQL compares against the session time zone. Use `timestamptz` to avoid ambiguity around time zones.

## Multiple application instances

Instances connected to the same database coordinate with a PostgreSQL advisory lock. One instance performs cleanup at a time. Other instances retry after the configured scan interval. The lock is held on the connection used for discovery and deletion. PgTtl releases it when its cleanup service stops; PostgreSQL also releases it if the database session ends unexpectedly.

The lock only coordinates PgTtl instances using the same lock key. It does not coordinate with other cleanup software.

## Development tests

The integration tests use Testcontainers and require Docker to be running:

```shell
dotnet test
```

## License

MIT. See [LICENSE](LICENSE).
