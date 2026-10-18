
namespace PgTtl.Options;

public sealed class PgTtlOptions
{   

    public string ConnectionString {get;set;} ="";
    public TimeSpan ScanInterval {get;set;} = TimeSpan.FromMinutes(5);

}
