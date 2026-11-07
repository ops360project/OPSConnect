using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpsConnect.Infrastructure.Persistence;

/// <summary>
/// Design-time factory supporting EF Core CLI migration commands.
/// </summary>
public class OpsConnectDbContextFactory : IDesignTimeDbContextFactory<OpsConnectDbContext>
{
    public const string DefaultLocalConnectionString =
        "Server=localhost;Database=OpsConnectDB;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";

    public OpsConnectDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("OPSCONNECT_CONNECTION_STRING")
                               ?? DefaultLocalConnectionString;

        var optionsBuilder = new DbContextOptionsBuilder<OpsConnectDbContext>();
        optionsBuilder.UseSqlServer(connectionString, sqlOptions =>
        {
            sqlOptions.MigrationsAssembly(typeof(OpsConnectDbContext).Assembly.FullName);
            sqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "admin");
        });

        return new OpsConnectDbContext(optionsBuilder.Options);
    }
}
