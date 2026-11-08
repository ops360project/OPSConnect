using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Organisation;

namespace OpsConnect.Application.Administration;

public sealed record ActivitySummaryDto(
    int ActivityId,
    string ActivityCode,
    string ActivityName,
    string? Description,
    bool IsActive,
    bool HasActiveReportingConfiguration,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

public sealed record ActivityKeyValueConfigDto(
    int ActivityConfigurationId,
    int ActivityId,
    string ConfigKey,
    string ConfigValue,
    string? Description,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

public sealed record ActivityReportingConfigurationDto(
    int ConfigurationId,
    int ActivityId,
    string ActivityCode,
    TimeOnly ReportingStartTime,
    TimeOnly ReportingEndTime,
    int ReportingIntervalMinutes,
    string TimeZoneId,
    bool IsReportingEnabled,
    bool StopWhenAllCompleted,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    bool IsCurrentOpenConfiguration,
    IReadOnlyList<TimeOnly> CalculatedDailySlotTimes,
    DateTime CreatedAtUtc,
    string? CreatedBy,
    DateTime? UpdatedAtUtc,
    string? UpdatedBy);

public sealed record ActivityDetailsDto(
    int ActivityId,
    string ActivityCode,
    string ActivityName,
    string? Description,
    string? ConfigurationJson,
    bool IsActive,
    ActivityReportingConfigurationDto? EffectiveReportingConfiguration,
    IReadOnlyList<ActivityKeyValueConfigDto> Settings,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

public sealed record ActivityReportingScheduleResultDto(
    int ActivityId,
    string ActivityCode,
    string ActivityName,
    bool IsActivityActive,
    DateOnly? AsOfReportingDate,
    ActivityReportingConfigurationDto? ResolvedConfigurationAsOfDate,
    IReadOnlyList<ActivityReportingConfigurationDto> ConfigurationHistory);

public sealed record UpsertActivityMasterRequest(
    string ActivityCode,
    string ActivityName,
    string? Description = null,
    string? ConfigurationJson = null,
    bool IsActive = true,
    string ChangedBy = "System",
    string? ChangeReason = null)
{
    public UpsertActivityMasterRequest ValidateAndNormalize()
    {
        var normalizedCode = ActivityMaster.ValidateActivityCode(ActivityCode);
        var normalizedName = OrganisationCodeNormalizer.NormalizeName(ActivityName, nameof(ActivityName));

        if (string.IsNullOrWhiteSpace(ChangedBy))
        {
            throw new ArgumentException("ChangedBy administrator identity is required.", nameof(ChangedBy));
        }

        return new UpsertActivityMasterRequest(
            ActivityCode: normalizedCode,
            ActivityName: normalizedName,
            Description: string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            ConfigurationJson: string.IsNullOrWhiteSpace(ConfigurationJson) ? null : ConfigurationJson.Trim(),
            IsActive: IsActive,
            ChangedBy: ChangedBy.Trim(),
            ChangeReason: string.IsNullOrWhiteSpace(ChangeReason) ? null : ChangeReason.Trim());
    }
}

public sealed record SetActivityKeyValueConfigRequest(
    string ActivityCode,
    string ConfigKey,
    string ConfigValue,
    string? Description = null,
    string ChangedBy = "System",
    string? ChangeReason = null)
{
    public SetActivityKeyValueConfigRequest ValidateAndNormalize()
    {
        var normalizedActivityCode = ActivityMaster.ValidateActivityCode(ActivityCode);
        var normalizedKey = OrganisationCodeNormalizer.NormalizeCode(ConfigKey, nameof(ConfigKey));

        if (normalizedKey.Length > 100)
        {
            throw new ArgumentException("ConfigKey cannot exceed 100 characters.", nameof(ConfigKey));
        }

        if (string.IsNullOrWhiteSpace(ConfigValue))
        {
            throw new ArgumentException("ConfigValue is required and cannot be empty or whitespace.", nameof(ConfigValue));
        }

        if (ConfigValue.Trim().Length > 2000)
        {
            throw new ArgumentException("ConfigValue cannot exceed 2000 characters.", nameof(ConfigValue));
        }

        if (string.IsNullOrWhiteSpace(ChangedBy))
        {
            throw new ArgumentException("ChangedBy administrator identity is required.", nameof(ChangedBy));
        }

        return new SetActivityKeyValueConfigRequest(
            ActivityCode: normalizedActivityCode,
            ConfigKey: normalizedKey,
            ConfigValue: ConfigValue.Trim(),
            Description: string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            ChangedBy: ChangedBy.Trim(),
            ChangeReason: string.IsNullOrWhiteSpace(ChangeReason) ? null : ChangeReason.Trim());
    }
}

public sealed record UpdateActivityReportingConfigurationRequest(
    string ActivityCode,
    TimeOnly ReportingStartTime,
    TimeOnly ReportingEndTime,
    int ReportingIntervalMinutes,
    string TimeZoneId,
    bool IsReportingEnabled,
    bool StopWhenAllCompleted,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo = null,
    string ChangedBy = "System",
    string? ChangeReason = null)
{
    public UpdateActivityReportingConfigurationRequest ValidateAndNormalize()
    {
        var normalizedActivityCode = ActivityMaster.ValidateActivityCode(ActivityCode);

        ActivityReportingConfiguration.ValidateSchedule(
            ReportingStartTime,
            ReportingEndTime,
            ReportingIntervalMinutes,
            TimeZoneId,
            EffectiveFrom,
            EffectiveTo);

        if (string.IsNullOrWhiteSpace(ChangedBy))
        {
            throw new ArgumentException("ChangedBy administrator identity is required.", nameof(ChangedBy));
        }

        return this with
        {
            ActivityCode = normalizedActivityCode,
            TimeZoneId = TimeZoneId.Trim(),
            ChangedBy = ChangedBy.Trim(),
            ChangeReason = string.IsNullOrWhiteSpace(ChangeReason) ? null : ChangeReason.Trim()
        };
    }
}

public sealed record ConfigurationHistoryEntryDto(
    long ConfigurationHistoryId,
    string EntityName,
    string EntityKey,
    string ActionType,
    string? PreviousValueJson,
    string? NewValueJson,
    string? ChangeReason,
    string ChangedBy,
    DateTime ChangedAtUtc);

public sealed record ActivityConfigurationOperationResult(
    bool IsSuccess,
    string? ErrorCode,
    string? ErrorMessage,
    ActivityDetailsDto? Activity);

public sealed record ReportingConfigurationUpdateResult(
    bool IsSuccess,
    string? ErrorCode,
    string? ErrorMessage,
    ActivityReportingConfigurationDto? ActiveConfiguration,
    ConfigurationHistoryEntryDto? AuditEntry);
