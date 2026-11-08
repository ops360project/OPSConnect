using Microsoft.Extensions.Logging;
using OpsConnect.Application.Reporting;

namespace OpsConnect.Infrastructure.Scheduling;

/// <summary>
/// Hangfire recurring background job that triggers the central scheduled reporting dispatcher.
/// </summary>
public sealed class ScheduledReportingBackgroundJob
{
    public const string RecurringJobId = "scheduled-reporting-dispatcher";

    private readonly IScheduledReportingService _scheduledReportingService;
    private readonly ILogger<ScheduledReportingBackgroundJob> _logger;

    public ScheduledReportingBackgroundJob(
        IScheduledReportingService scheduledReportingService,
        ILogger<ScheduledReportingBackgroundJob> logger)
    {
        _scheduledReportingService = scheduledReportingService;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Scheduled reporting background job tick executing...");
        var result = await _scheduledReportingService.DispatchDueReportsAsync(asOfUtc: null, cancellationToken);
        _logger.LogInformation(
            "Scheduled reporting background job completed: {Evaluated} slots evaluated, {Processed} delivered, {Failed} failed.",
            result.SlotsEvaluated,
            result.SlotsClaimedAndProcessed,
            result.SlotsFailed);
    }
}
