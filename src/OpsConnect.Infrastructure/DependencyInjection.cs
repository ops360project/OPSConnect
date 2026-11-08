using Hangfire;
using Hangfire.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Activities.RFFO;
using OpsConnect.Application.Administration;
using OpsConnect.Application.Organisation;
using OpsConnect.Application.Reporting;
using OpsConnect.Application.Telegram;
using OpsConnect.Infrastructure.Persistence;
using OpsConnect.Infrastructure.Scheduling;
using OpsConnect.Infrastructure.Services;
using OpsConnect.Infrastructure.Telegram;

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
        services.AddScoped<IActivityReportingService, ActivityReportingService>();
        services.AddSingleton<ITelegramReportFormatter, TelegramReportFormatter>();
        services.AddScoped<IScheduledReportingService, ScheduledReportingService>();
        services.AddTransient<ScheduledReportingBackgroundJob>();

        services.AddOptions<TelegramBotOptions>()
            .Configure(options => BindTelegramBotOptions(configuration, options))
            .ValidateOnStart();

        services.AddScoped<ITelegramGroupAuthorizationService, TelegramGroupAuthorizationService>();
        services.AddScoped<ITelegramMessageIngestionService, TelegramMessageIngestionService>();
        services.AddScoped<ITelegramOperationalMonitoringService, TelegramOperationalMonitoringService>();
        services.AddHttpClient<ITelegramBotClient, TelegramBotHttpClient>();

        return services;
    }

    public static void BindTelegramBotOptions(IConfiguration configuration, TelegramBotOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        var section = configuration.GetSection(TelegramBotOptions.SectionName);
        if (!section.Exists())
        {
            return;
        }

        if (bool.TryParse(section[nameof(TelegramBotOptions.IsEnabled)], out var isEnabled))
        {
            options.IsEnabled = isEnabled;
        }

        options.BotToken = section[nameof(TelegramBotOptions.BotToken)];
        options.WebhookUrl = section[nameof(TelegramBotOptions.WebhookUrl)];
        options.WebhookSecretToken = section[nameof(TelegramBotOptions.WebhookSecretToken)];

        if (int.TryParse(section[nameof(TelegramBotOptions.MaxWebhookPayloadBytes)], out var maxBytes))
        {
            options.MaxWebhookPayloadBytes = maxBytes;
        }

        options.AuthorizedSourceChatIds = ReadChatIds(section.GetSection(nameof(TelegramBotOptions.AuthorizedSourceChatIds)));
        options.DestinationChatIds = ReadChatIds(section.GetSection(nameof(TelegramBotOptions.DestinationChatIds)));
    }

    private static List<long> ReadChatIds(IConfigurationSection section)
    {
        var list = new List<long>();
        foreach (var child in section.GetChildren())
        {
            if (long.TryParse(child.Value, out var chatId))
            {
                list.Add(chatId);
            }
        }

        return list;
    }

    public static IServiceCollection AddHangfireServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddHangfire(config => config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
                {
                    CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                    SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                    QueuePollInterval = TimeSpan.Zero,
                    UseRecommendedIsolationLevel = true,
                    DisableGlobalLocks = true,
                    SchemaName = "admin"
                }));
        }

        return services;
    }
}
