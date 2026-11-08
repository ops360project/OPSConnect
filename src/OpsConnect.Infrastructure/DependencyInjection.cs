using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Activities.RFFO;
using OpsConnect.Application.Administration;
using OpsConnect.Application.Organisation;
using OpsConnect.Infrastructure.Persistence;
using OpsConnect.Infrastructure.Services;

namespace OpsConnect.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "OpsConnectDb";

    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Database connection string '{ConnectionStringName}' is not configured.");

        services.AddDbContext<OpsConnectDbContext>(options =>
        {
            options.UseSqlServer(connectionString, sqlOptions =>
            {
                sqlOptions.MigrationsAssembly(typeof(OpsConnectDbContext).Assembly.FullName);
                sqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "admin");
                sqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null);
            });
        });

        services.AddSingleton<IOrganisationExcelReader, OrganisationExcelReader>();
        services.AddScoped<IOrganisationMasterService, OrganisationMasterService>();

        services.AddScoped<RffoActivityUpdateHandler>();
        services.AddScoped<IRffoActivityUpdateHandler>(sp => sp.GetRequiredService<RffoActivityUpdateHandler>());
        services.AddScoped<IActivityUpdateHandler>(sp => sp.GetRequiredService<RffoActivityUpdateHandler>());
        services.AddScoped<IActivityStatusQueryService, ActivityStatusQueryService>();
        services.AddScoped<IStatusReopenPermissionService, StatusReopenPermissionService>();
        services.AddScoped<IActivityConfigurationService, ActivityConfigurationService>();

        return services;
    }
}
