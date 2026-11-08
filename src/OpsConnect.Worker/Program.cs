using Hangfire;
using OpsConnect.Application;
using OpsConnect.Infrastructure;
using OpsConnect.Infrastructure.Scheduling;
using OpsConnect.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);
builder.Services.AddHangfireServices(builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString(OpsConnect.Infrastructure.DependencyInjection.ConnectionStringName);
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddHangfireServer(options =>
    {
        options.WorkerCount = 2;
        options.ServerName = "OpsConnect-ReportingWorker";
    });
}

builder.Services.AddHostedService<WorkerHeartbeatService>();

var host = builder.Build();

if (!string.IsNullOrWhiteSpace(connectionString))
{
    using var scope = host.Services.CreateScope();
    var recurringJobManager = scope.ServiceProvider.GetService<IRecurringJobManager>();
    recurringJobManager?.AddOrUpdate<ScheduledReportingBackgroundJob>(
        ScheduledReportingBackgroundJob.RecurringJobId,
        job => job.ExecuteAsync(CancellationToken.None),
        Cron.Minutely);
}

host.Run();
