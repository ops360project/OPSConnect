using OpsConnect.Domain.Administration;

namespace OpsConnect.Api.Models;

/// <summary>
/// Request payload for submitting an operational activity status message via REST API.
/// </summary>
public sealed record SubmitActivityUpdateApiRequest(
    string? RawMessageText,
    string? ActivityCode = null,
    long? TelegramMessageLogId = null,
    long? TelegramChatId = null,
    long? TelegramMessageId = null,
    long? TelegramUserId = null,
    string? SenderUsername = null,
    string? SenderDisplayName = null,
    DateTime? OccurredAtUtc = null,
    DateOnly? ReportingDate = null);

/// <summary>
/// Request payload to grant a single-use same-day reopening permission.
/// Audit identity (EnabledBy) is derived strictly from the authenticated user principal.
/// </summary>
public sealed record GrantReopenPermissionApiRequest(
    string ActivityCode,
    string BranchCode,
    string Reason,
    DateOnly? ReportingDate = null);

/// <summary>
/// Request payload to revoke an Available reopening permission.
/// Audit identity (RevokedBy) is derived strictly from the authenticated user principal.
/// </summary>
public sealed record RevokeReopenPermissionApiRequest(
    string Reason);

/// <summary>
/// Request payload to expire stale Available permissions from prior reporting dates.
/// </summary>
public sealed record ExpireStalePermissionsApiRequest(
    string? ActivityCode = null,
    DateOnly? AsOfReportingDate = null);

/// <summary>
/// Response payload reporting the count of expired stale reopening permissions.
/// </summary>
public sealed record ExpireStalePermissionsApiResponse(
    int ExpiredCount,
    string? ActivityCode,
    DateOnly? AsOfReportingDate,
    string TriggeredBy);

/// <summary>
/// Request payload to create or update an ActivityMaster record.
/// Audit identity (ChangedBy) is derived strictly from the authenticated user principal.
/// </summary>
public sealed record UpsertActivityApiRequest(
    string ActivityName,
    string? ActivityCode = null,
    string? Description = null,
    string? ConfigurationJson = null,
    bool IsActive = true,
    string? ChangeReason = null);

/// <summary>
/// Request payload to enable or disable an activity.
/// Audit identity (ChangedBy) is derived strictly from the authenticated user principal.
/// </summary>
public sealed record SetActivityActiveStateApiRequest(
    bool IsActive,
    string? ChangeReason = null);

/// <summary>
/// Request payload to create or update an activity key-value setting.
/// Audit identity (ChangedBy) is derived strictly from the authenticated user principal.
/// </summary>
public sealed record SetActivitySettingApiRequest(
    string ConfigValue,
    string? Description = null,
    string? ChangeReason = null);

/// <summary>
/// Request payload to create or update an effective-dated activity reporting schedule.
/// Audit identity (ChangedBy) is derived strictly from the authenticated user principal.
/// </summary>
public sealed record UpdateReportingScheduleApiRequest(
    TimeOnly ReportingStartTime,
    TimeOnly ReportingEndTime,
    int ReportingIntervalMinutes,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo = null,
    string TimeZoneId = ActivityReportingConfiguration.DefaultTimeZoneId,
    bool IsReportingEnabled = true,
    bool StopWhenAllCompleted = true,
    string? ChangeReason = null,
    bool AutoClosePreviousOpenPeriod = true);

/// <summary>
/// Request payload to validate schedule parameters and preview calculated daily slot times.
/// </summary>
public sealed record PreviewReportingSlotsApiRequest(
    TimeOnly ReportingStartTime,
    TimeOnly ReportingEndTime,
    int ReportingIntervalMinutes,
    string TimeZoneId = ActivityReportingConfiguration.DefaultTimeZoneId);
