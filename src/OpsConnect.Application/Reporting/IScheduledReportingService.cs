namespace OpsConnect.Application.Reporting;

/// <summary>
/// Service responsible for managing durable scheduled reporting slots, generating immutable report snapshots,
/// and delivering scheduled Telegram reports to configured active destination groups.
/// </summary>
public interface IScheduledReportingService
{
    /// <summary>
    /// Evaluates active activities and their effective schedules, ensures daily reporting cycles and slots exist,
    /// claims due slots in a concurrency-safe manner, and processes/delivers reports.
    /// </summary>
    Task<ScheduledReportingDispatchResult> DispatchDueReportsAsync(
        DateTime? asOfUtc = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes or retries a specific scheduled reporting slot idempotently.
    /// </summary>
    Task<SlotExecutionResult> ExecuteSlotAsync(
        long reportingSlotId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the durable reporting cycle status for an activity on a specific reporting date.
    /// </summary>
    Task<ScheduledReportingCycleStatusDto?> GetReportingCycleStatusAsync(
        string activityCode,
        DateOnly? reportingDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates whether all active mapped branches have reached Completed status for the given activity and date,
    /// generating and delivering an immediate final report snapshot and closing the reporting cycle.
    /// </summary>
    Task<ImmediateFinalReportResult> ExecuteImmediateFinalReportAsync(
        string activityCode,
        DateOnly? reportingDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the scheduled reporting slots and their delivery records for an activity on a specific reporting date.
    /// </summary>
    Task<IReadOnlyList<ScheduledSlotStatusDto>> GetScheduledSlotsAsync(
        string activityCode,
        DateOnly? reportingDate = null,
        CancellationToken cancellationToken = default);
}
