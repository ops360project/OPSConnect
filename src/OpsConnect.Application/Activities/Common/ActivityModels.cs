using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Organisation;
using OpsConnect.Domain.Telegram;

namespace OpsConnect.Application.Activities.Common;

/// <summary>
/// Represents an unparsed incoming operational activity message along with optional sender audit metadata.
/// </summary>
public sealed record RawActivityMessageSubmissionRequest(
    string? RawMessageText,
    long? TelegramMessageLogId = null,
    long? TelegramChatId = null,
    long? TelegramMessageId = null,
    long? TelegramUserId = null,
    string? SenderUsername = null,
    string? SenderDisplayName = null,
    DateTime? OccurredAtUtc = null,
    DateOnly? ExplicitReportingDate = null,
    string? ExpectedActivityCode = null);

/// <summary>
/// Validated, normalized command dispatched by IActivityUpdateProcessor to a pluggable IActivityUpdateHandler.
/// </summary>
public sealed record ActivityUpdateSubmissionCommand(
    string ActivityCode,
    string BranchCode,
    BranchActivityStatus IncomingStatus,
    long? TelegramMessageLogId = null,
    long? TelegramChatId = null,
    long? TelegramMessageId = null,
    long? TelegramUserId = null,
    string? SenderUsername = null,
    string? SenderDisplayName = null,
    DateTime? OccurredAtUtc = null,
    DateOnly? ExplicitReportingDate = null,
    PendingReasonCode? PendingReason = null,
    string? PendingReasonDescription = null)
{
    public ActivityUpdateSubmissionCommand ValidateAndNormalize()
    {
        var normalizedActivityCode = ActivityMaster.ValidateActivityCode(ActivityCode);
        var normalizedBranchCode = OrganisationCodeNormalizer.NormalizeAndValidateBranchCode(BranchCode, nameof(BranchCode));

        if (IncomingStatus is not (BranchActivityStatus.Pending or BranchActivityStatus.Completed))
        {
            throw new ArgumentException(
                $"IncomingStatus '{IncomingStatus}' is invalid. Only 'Pending' and 'Completed' are permitted for activity submissions.",
                nameof(IncomingStatus));
        }

        if (IncomingStatus == BranchActivityStatus.Pending)
        {
            if (!PendingReason.HasValue || PendingReason.Value == PendingReasonCode.None || !Enum.IsDefined(PendingReason.Value))
            {
                throw new ArgumentException(
                    "A valid PendingReason is required when IncomingStatus is Pending.",
                    nameof(PendingReason));
            }

            if (PendingReason.Value == PendingReasonCode.Other && string.IsNullOrWhiteSpace(PendingReasonDescription))
            {
                throw new ArgumentException(
                    "PendingReasonDescription is mandatory when PendingReason is Other.",
                    nameof(PendingReasonDescription));
            }

            if (PendingReasonDescription is not null && PendingReasonDescription.Length > PendingReasonHelper.MaxDescriptionLength)
            {
                throw new ArgumentException(
                    $"PendingReasonDescription cannot exceed {PendingReasonHelper.MaxDescriptionLength} characters.",
                    nameof(PendingReasonDescription));
            }
        }

        return this with
        {
            ActivityCode = normalizedActivityCode,
            BranchCode = normalizedBranchCode,
            SenderUsername = string.IsNullOrWhiteSpace(SenderUsername) ? null : SenderUsername.Trim(),
            SenderDisplayName = string.IsNullOrWhiteSpace(SenderDisplayName) ? null : SenderDisplayName.Trim(),
            PendingReasonDescription = string.IsNullOrWhiteSpace(PendingReasonDescription) ? null : PendingReasonDescription.Trim()
        };
    }
}

/// <summary>
/// Result of processing an operational activity status submission.
/// </summary>
public sealed record ActivityUpdateProcessingResult(
    bool IsAccepted,
    MessageProcessingStatus ProcessingStatus,
    TransitionOutcome Outcome,
    string? ActivityCode,
    int? ActivityId,
    string? BranchCode,
    int? BranchId,
    DateOnly? ReportingDate,
    BranchActivityStatus? PreviousStatus,
    BranchActivityStatus? CurrentStatus,
    bool DidStatusChange,
    bool DidConsumeReopenPermission,
    long? ConsumedPermissionId,
    bool DidReactivateReportingCycle,
    bool DidTriggerImmediateFinalReportCondition,
    string? RejectionReason,
    string ReplyMessage,
    IReadOnlyList<TelegramMessageParseError>? ParseErrors = null);

/// <summary>
/// Query filter for retrieving branch-wise activity status for a reporting date.
/// </summary>
public sealed record ActivityStatusFilterQuery(
    string ActivityCode = ActivityModuleConstants.RffoActivityCode,
    DateOnly? ReportingDate = null,
    string? ZoneCode = null,
    string? StateCode = null,
    string? RegionCode = null,
    BranchActivityStatus? Status = null,
    string? SearchTerm = null,
    string? BranchCode = null,
    int? Page = null,
    int? PageSize = null)
{
    public ActivityStatusFilterQuery Normalize()
    {
        if (Page.HasValue && Page.Value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Page), "Page must be at least 1.");
        }

        if (PageSize.HasValue && (PageSize.Value < 1 || PageSize.Value > 500))
        {
            throw new ArgumentOutOfRangeException(nameof(PageSize), "PageSize must be between 1 and 500.");
        }

        return this with
        {
            ActivityCode = ActivityMaster.ValidateActivityCode(ActivityCode),
            ZoneCode = string.IsNullOrWhiteSpace(ZoneCode) ? null : OrganisationCodeNormalizer.NormalizeCode(ZoneCode, nameof(ZoneCode)),
            StateCode = string.IsNullOrWhiteSpace(StateCode) ? null : OrganisationCodeNormalizer.NormalizeCode(StateCode, nameof(StateCode)),
            RegionCode = string.IsNullOrWhiteSpace(RegionCode) ? null : OrganisationCodeNormalizer.NormalizeCode(RegionCode, nameof(RegionCode)),
            BranchCode = string.IsNullOrWhiteSpace(BranchCode) ? null : OrganisationCodeNormalizer.NormalizeAndValidateBranchCode(BranchCode, nameof(BranchCode)),
            SearchTerm = string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm.Trim()
        };
    }
}

/// <summary>
/// Represents the status of a single active mapped branch on a reporting date.
/// When a branch has not submitted an update on the reporting date, CurrentStatus is NotReported and IsDerivedNotReported is true.
/// </summary>
public sealed record BranchActivityStatusItemDto(
    int BranchId,
    string BranchCode,
    string BranchName,
    int RegionId,
    string RegionCode,
    string RegionName,
    int StateId,
    string StateCode,
    string StateName,
    int ZoneId,
    string ZoneCode,
    string ZoneName,
    DateOnly ReportingDate,
    BranchActivityStatus CurrentStatus,
    bool IsDerivedNotReported,
    int ReopenedCount,
    bool HasAvailableReopenPermission,
    long? LastTelegramMessageLogId,
    long? LastUpdatedByTelegramUserId,
    string? LastUpdatedByDisplayName,
    DateTime? LastUpdatedAtUtc,
    PendingReasonCode? CurrentPendingReason = null,
    string? CurrentPendingReasonDescription = null);

/// <summary>
/// Snapshot of branch-wise statuses and reconciled summary counts for an activity on a reporting date.
/// Always satisfies: CompletedBranches + PendingBranches + NotReportedBranches == TotalActiveBranches.
/// </summary>
public sealed record ActivityDailyStatusSnapshotDto(
    int ActivityId,
    string ActivityCode,
    string ActivityName,
    DateOnly ReportingDate,
    string TimeZoneId,
    int TotalActiveBranches,
    int CompletedBranches,
    int PendingBranches,
    int NotReportedBranches,
    decimal CompletionPercentage,
    IReadOnlyList<BranchActivityStatusItemDto> Branches);

/// <summary>
/// Single immutable audit entry from activity.ActivityHistory.
/// </summary>
public sealed record ActivityHistoryEntryDto(
    long ActivityHistoryId,
    int ActivityId,
    string ActivityCode,
    int BranchId,
    string BranchCode,
    DateOnly ReportingDate,
    BranchActivityStatus PreviousStatus,
    BranchActivityStatus NewStatus,
    TransitionOutcome Outcome,
    long? TelegramMessageLogId,
    long? ConsumedReopenPermissionId,
    long? TelegramUserId,
    string? TelegramUsername,
    string? TelegramDisplayName,
    string? Notes,
    DateTime RecordedAtUtc,
    PendingReasonCode? PendingReason = null,
    string? PendingReasonDescription = null);

/// <summary>
/// Branch status and chronological audit history for a specific activity and reporting date (or across dates).
/// </summary>
public sealed record BranchActivityHistoryResultDto(
    int ActivityId,
    string ActivityCode,
    int BranchId,
    string BranchCode,
    string BranchName,
    bool IsBranchActive,
    DateOnly ReportingDate,
    BranchActivityStatus CurrentStatusOnDate,
    bool IsDerivedNotReported,
    int ReopenedCountOnDate,
    bool HasAvailableReopenPermissionOnDate,
    IReadOnlyList<ActivityHistoryEntryDto> HistoryEntries);

/// <summary>
/// Request to grant a single-use, same-day administrator reopening permission.
/// </summary>
public sealed record GrantStatusReopenPermissionRequest(
    string ActivityCode,
    string BranchCode,
    DateOnly? ReportingDate,
    string EnabledBy,
    string Reason)
{
    public GrantStatusReopenPermissionRequest ValidateAndNormalize()
    {
        var normalizedActivityCode = ActivityMaster.ValidateActivityCode(ActivityCode);
        var normalizedBranchCode = OrganisationCodeNormalizer.NormalizeAndValidateBranchCode(BranchCode, nameof(BranchCode));

        if (string.IsNullOrWhiteSpace(EnabledBy))
        {
            throw new ArgumentException("Administrator identity (EnabledBy) is mandatory.", nameof(EnabledBy));
        }

        if (string.IsNullOrWhiteSpace(Reason))
        {
            throw new ArgumentException("A mandatory reason must be provided to grant reopening permission.", nameof(Reason));
        }

        return new GrantStatusReopenPermissionRequest(
            ActivityCode: normalizedActivityCode,
            BranchCode: normalizedBranchCode,
            ReportingDate: ReportingDate,
            EnabledBy: EnabledBy.Trim(),
            Reason: Reason.Trim());
    }
}

/// <summary>
/// Request to revoke an Available administrator reopening permission before it is consumed.
/// </summary>
public sealed record RevokeStatusReopenPermissionRequest(
    long PermissionId,
    string RevokedBy,
    string Reason)
{
    public RevokeStatusReopenPermissionRequest ValidateAndNormalize()
    {
        if (PermissionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PermissionId), "PermissionId must be positive.");
        }

        if (string.IsNullOrWhiteSpace(RevokedBy))
        {
            throw new ArgumentException("Administrator identity (RevokedBy) is mandatory.", nameof(RevokedBy));
        }

        if (string.IsNullOrWhiteSpace(Reason))
        {
            throw new ArgumentException("A mandatory reason must be provided to revoke a reopening permission.", nameof(Reason));
        }

        return new RevokeStatusReopenPermissionRequest(
            PermissionId: PermissionId,
            RevokedBy: RevokedBy.Trim(),
            Reason: Reason.Trim());
    }
}

/// <summary>
/// Filter query for listing administrator status reopening permissions and audit history.
/// </summary>
public sealed record StatusReopenPermissionFilterQuery(
    string? ActivityCode = ActivityModuleConstants.RffoActivityCode,
    string? BranchCode = null,
    DateOnly? ReportingDate = null,
    ReopenPermissionStatus? Status = null)
{
    public StatusReopenPermissionFilterQuery Normalize() =>
        new(
            ActivityCode: string.IsNullOrWhiteSpace(ActivityCode) ? null : ActivityMaster.ValidateActivityCode(ActivityCode),
            BranchCode: string.IsNullOrWhiteSpace(BranchCode) ? null : OrganisationCodeNormalizer.NormalizeAndValidateBranchCode(BranchCode, nameof(BranchCode)),
            ReportingDate: ReportingDate,
            Status: Status);
}

/// <summary>
/// DTO representing a status reopening permission record and its lifecycle audit metadata.
/// </summary>
public sealed record StatusReopenPermissionDto(
    long PermissionId,
    int ActivityId,
    string ActivityCode,
    int BranchId,
    string BranchCode,
    string BranchName,
    DateOnly ReportingDate,
    ReopenPermissionStatus PermissionStatus,
    string EnabledBy,
    DateTime EnabledAtUtc,
    string Reason,
    DateTime? ConsumedAtUtc,
    long? ConsumedByTelegramUserId,
    long? ConsumedMessageId,
    string? RevokedBy,
    DateTime? RevokedAtUtc,
    string? RevocationReason);

public sealed record StatusReopenPermissionOperationResult(
    bool IsSuccess,
    string? ErrorCode,
    string? ErrorMessage,
    StatusReopenPermissionDto? Permission);
