using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgTtl.DependencyInjection;
using PgTtl.Options;

public sealed class PgTtlRegistrationTests
{
    [Fact]
    public void AddPgTtl_RequiresAConnectionString()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddPgTtl(options => options.ScanInterval = TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddPgTtl_RequiresPositiveScanInterval(int minutes)
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddPgTtl(options =>
        {
            options.ConnectionString = "Host=localhost;Database=pgttl;Username=test;Password=test";
            options.ScanInterval = TimeSpan.FromMinutes(minutes);
        }));
    }

    [Fact]
    public void AddPgTtl_RegistersOptionsAndHostedService()
    {
        var services = new ServiceCollection();
        services.AddPgTtl(options =>
        {
            options.ConnectionString = "Host=localhost;Database=pgttl;Username=test;Password=test";
            options.ScanInterval = TimeSpan.FromSeconds(30);
        });

        var options = Assert.IsType<PgTtlOptions>(
            Assert.Single(services, descriptor =>
                descriptor.ServiceType == typeof(PgTtlOptions)).ImplementationInstance);

        Assert.Equal(TimeSpan.FromSeconds(30), options.ScanInterval);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService));
    }
}
