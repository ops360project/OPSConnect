using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Common;
using OpsConnect.Application.Organisation;
using OpsConnect.Application.Telegram;

namespace OpsConnect.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<IBusinessClock, SystemBusinessClock>();
        services.AddSingleton<IOrganisationHierarchyImportValidator, OrganisationHierarchyImportValidator>();
        services.AddSingleton<ITelegramActivityMessageParser, TelegramActivityMessageParser>();
        services.AddScoped<IActivityUpdateProcessor, ActivityUpdateProcessor>();

        services.AddSingleton<IValidateOptions<TelegramBotOptions>, TelegramBotOptionsValidator>();
        services.AddSingleton<ITelegramUpdateNormalizer, TelegramUpdateNormalizer>();
        services.AddScoped<ITelegramWebhookReceiver, TelegramWebhookReceiver>();

        return services;
    }
}
