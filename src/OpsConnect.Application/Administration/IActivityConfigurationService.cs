namespace OpsConnect.Application.Administration;

/// <summary>
/// Application service contract for managing ActivityMaster definitions, activity key-value settings,
/// effective-dated ActivityReportingConfiguration schedules, and immutable ConfigurationHistory audit logs.
/// </summary>
public interface IActivityConfigurationService
{
    Task<IReadOnlyList<ActivitySummaryDto>> GetActivitiesAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default);

    Task<ActivityDetailsDto?> GetActivityByCodeAsync(
        string activityCode,
        DateOnly? asOfReportingDate = null,
        CancellationToken cancellationToken = default);

    Task<ActivityConfigurationOperationResult> UpsertActivityAsync(
        UpsertActivityMasterRequest request,
        CancellationToken cancellationToken = default);

    Task<ActivityConfigurationOperationResult> SetActivityActiveStateAsync(
        SetActivityActiveStateRequest request,
        CancellationToken cancellationToken = default);

    Task<ActivityConfigurationOperationResult> SetActivityConfigurationValueAsync(
        SetActivityKeyValueConfigRequest request,
        CancellationToken cancellationToken = default);

    Task<ActivityReportingScheduleResultDto?> GetReportingConfigurationAsync(
        string activityCode,
        DateOnly? asOfReportingDate = null,
        CancellationToken cancellationToken = default);

    Task<ReportingConfigurationUpdateResult> UpdateReportingConfigurationAsync(
        UpdateActivityReportingConfigurationRequest request,
        CancellationToken cancellationToken = default);

    ReportingSlotsPreviewResultDto PreviewReportingSlots(
        PreviewReportingSlotsRequest request);

    Task<IReadOnlyList<ConfigurationHistoryEntryDto>> GetConfigurationHistoryAsync(
        string? entityName = null,
        string? entityKey = null,
        CancellationToken cancellationToken = default);
}
