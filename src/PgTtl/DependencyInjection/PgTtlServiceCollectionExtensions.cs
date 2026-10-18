using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using PgTtl.Internal;
using PgTtl.Options;

namespace PgTtl.DependencyInjection;

public static class PgTtlServiceCollectionExtensions
{
    public static IServiceCollection AddPgTtl(
        this IServiceCollection services,
        Action<PgTtlOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        var options = new PgTtlOptions();
        configureOptions(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException(
                "A PostgreSQL connection string must be provided.",
                nameof(configureOptions));
        }

        if (options.ScanInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.ScanInterval),
                "The scan interval must be greater than zero.");
        }

        services.AddSingleton(options);
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(options.ConnectionString));
        services.AddSingleton<ExpiredRowCleaner>();
        services.AddHostedService<PgTtlHostedService>();
        return services;
    }
}
