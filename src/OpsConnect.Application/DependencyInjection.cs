using Microsoft.Extensions.DependencyInjection;
using OpsConnect.Application.Common;
using OpsConnect.Application.Organisation;

namespace OpsConnect.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<IBusinessClock, SystemBusinessClock>();
        services.AddSingleton<IOrganisationHierarchyImportValidator, OrganisationHierarchyImportValidator>();
        return services;
    }
}
