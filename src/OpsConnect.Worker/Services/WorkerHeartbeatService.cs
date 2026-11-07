using OpsConnect.Application.Common;

namespace OpsConnect.Worker.Services;

/// <summary>
/// Foundation background worker host service.
/// Scheduled reporting and Hangfire job processing will be wired in Phase 6.
/// </summary>
public class WorkerHeartbeatService : BackgroundService
{
    private readonly ILogger<WorkerHeartbeatService> _logger;
    private readonly IBusinessClock _businessClock;

    public WorkerHeartbeatService(
        ILogger<WorkerHeartbeatService> logger,
        IBusinessClock businessClock)
    {
        _logger = logger;
        _businessClock = businessClock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OpsConnect.Worker started at {UtcNow} (Business Date Asia/Kolkata: {BusinessDate}).",
            _businessClock.UtcNow,
            _businessClock.GetBusinessDate("Asia/Kolkata"));

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
