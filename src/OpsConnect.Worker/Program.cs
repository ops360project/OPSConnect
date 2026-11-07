using OpsConnect.Application;
using OpsConnect.Infrastructure;
using OpsConnect.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);
builder.Services.AddHostedService<WorkerHeartbeatService>();

var host = builder.Build();
host.Run();
