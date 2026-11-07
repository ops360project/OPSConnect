using Microsoft.Extensions.DependencyInjection;
using OpsConnect.Application.Common;

namespace OpsConnect.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<IBusinessClock, SystemBusinessClock>();
        return services;
    }
}
