using OpsConnect.Domain.Common;
using OpsConnect.Domain.Organisation;

namespace OpsConnect.Domain.Activities;

public enum BranchActivityStatus
{
    NotReported = 0,
    Pending = 1,
    Completed = 2
}

public enum ReopenPermissionStatus
{
    Available = 1,
    Consumed = 2,
    Expired = 3,
    Revoked = 4
}

public enum TransitionOutcome
{
    Accepted = 1,
    AcceptedUnchanged = 2,
    AcceptedViaReopen = 3,
    Rejected = 4
}

public sealed record StatusTransitionResult(
    bool IsAccepted,
    TransitionOutcome Outcome,
    BranchActivityStatus PreviousStatus,
    BranchActivityStatus NewStatus,
    bool DidStatusChange,
    bool DidConsumeReopenPermission,
    long? ConsumedPermissionId,
    string? RejectionReason);

public class ActivityMaster : ActivatableAuditableEntity
{
    public int ActivityId { get; set; }
    public string ActivityCode { get; set; } = string.Empty;
    public string ActivityName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ConfigurationJson { get; set; }

    public ICollection<ActivityStatus> ActivityStatuses { get; set; } = new List<ActivityStatus>();
    public ICollection<ActivityHistory> ActivityHistories { get; set; } = new List<ActivityHistory>();
    public ICollection<StatusReopenPermission> ReopenPermissions { get; set; } = new List<StatusReopenPermission>();

    public static ActivityMaster Create(
        string activityCode,
        string activityName,
        string? description = null,
        string? configurationJson = null,
        bool isActive = true,
        string? createdBy = null,
        DateTime? createdAtUtc = null)
    {
        var normalizedCode = ValidateActivityCode(activityCode);
        var normalizedName = OrganisationCodeNormalizer.NormalizeName(activityName, nameof(activityName));

        return new ActivityMaster
        {
            ActivityCode = normalizedCode,
            ActivityName = normalizedName,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            ConfigurationJson = string.IsNullOrWhiteSpace(configurationJson) ? null : configurationJson.Trim(),
            IsActive = isActive,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim(),
            CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow
        };
    }

    public void UpdateDetails(
        string activityCode,
        string activityName,
        string? description = null,
        string? configurationJson = null,
        bool isActive = true,
        string? updatedBy = null,
        DateTime? updatedAtUtc = null)
    {
        ActivityCode = ValidateActivityCode(activityCode);
        ActivityName = OrganisationCodeNormalizer.NormalizeName(activityName, nameof(activityName));
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        ConfigurationJson = string.IsNullOrWhiteSpace(configurationJson) ? null : configurationJson.Trim();
        IsActive = isActive;
        UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy.Trim();
        UpdatedAtUtc = updatedAtUtc ?? DateTime.UtcNow;
    }

    public static string ValidateActivityCode(string? activityCode)
    {
        var normalized = OrganisationCodeNormalizer.NormalizeCode(activityCode, nameof(activityCode));
        if (normalized.Length > 50)
        {
            throw new ArgumentException("ActivityCode cannot exceed 50 characters.", nameof(activityCode));
        }

        foreach (var ch in normalized)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_' && ch != '-')
            {
                throw new ArgumentException(
                    $"ActivityCode ('{activityCode}') contains invalid character '{ch}'. Only letters, digits, underscores, and hyphens are allowed.",
                    nameof(activityCode));
            }
        }

        return normalized;
    }
}

/// <summary>
/// Tracks the current status of a specific branch for an activity on a specific reporting date.
/// Protected by a SQL Server rowversion concurrency token.
/// </summary>
public class ActivityStatus : AuditableEntity
{
    public long ActivityStatusId { get; set; }
    public int ActivityId { get; set; }
    public int BranchId { get; set; }
    public DateOnly ReportingDate { get; set; }
    public BranchActivityStatus CurrentStatus { get; set; } = BranchActivityStatus.NotReported;

    public long? LastTelegramMessageLogId { get; set; }
    public long? LastUpdatedByTelegramUserId { get; set; }
    public string? LastUpdatedByDisplayName { get; set; }
    public int ReopenedCount { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public ActivityMaster Activity { get; set; } = null!;
    public BranchMaster Branch { get; set; } = null!;

    /// <summary>
    /// Evaluates and applies a status transition according to OpsConnect RFFO status rules
    /// and single-use administrator reopening permissions.
    /// </summary>
    public StatusTransitionResult ApplyTransition(
        BranchActivityStatus incomingStatus,
        DateOnly currentReportingDate,
        long telegramMessageLogId,
        long? telegramUserId,
        string? senderDisplayName,
        DateTime occurredAtUtc,
        StatusReopenPermission? reopenPermission = null)
    {
        if (incomingStatus == BranchActivityStatus.NotReported)
        {
            return new StatusTransitionResult(
                IsAccepted: false,
                Outcome: TransitionOutcome.Rejected,
                PreviousStatus: CurrentStatus,
                NewStatus: CurrentStatus,
                DidStatusChange: false,
                DidConsumeReopenPermission: false,
                ConsumedPermissionId: null,
                RejectionReason: "Branches cannot submit NotReported as an incoming status.");
        }

        var previousStatus = CurrentStatus;

        // Unchanged status submissions (Pending -> Pending or Completed -> Completed)
        if (previousStatus == incomingStatus)
        {
            return new StatusTransitionResult(
                IsAccepted: true,
                Outcome: TransitionOutcome.AcceptedUnchanged,
                PreviousStatus: previousStatus,
                NewStatus: CurrentStatus,
                DidStatusChange: false,
                DidConsumeReopenPermission: false,
                ConsumedPermissionId: null,
                RejectionReason: null);
        }

        // Standard forward transitions: NotReported -> Pending, NotReported -> Completed, Pending -> Completed
        if (previousStatus == BranchActivityStatus.NotReported ||
            (previousStatus == BranchActivityStatus.Pending && incomingStatus == BranchActivityStatus.Completed))
        {
            CurrentStatus = incomingStatus;
            LastTelegramMessageLogId = telegramMessageLogId;
            LastUpdatedByTelegramUserId = telegramUserId;
            LastUpdatedByDisplayName = senderDisplayName;
            UpdatedAtUtc = occurredAtUtc;

            return new StatusTransitionResult(
                IsAccepted: true,
                Outcome: TransitionOutcome.Accepted,
                PreviousStatus: previousStatus,
                NewStatus: CurrentStatus,
                DidStatusChange: true,
                DidConsumeReopenPermission: false,
                ConsumedPermissionId: null,
                RejectionReason: null);
        }

        // Completed -> Pending requires a valid, unconsumed, same-day administrator reopening permission
        if (previousStatus == BranchActivityStatus.Completed && incomingStatus == BranchActivityStatus.Pending)
        {
            if (reopenPermission is null)
            {
                return new StatusTransitionResult(
                    IsAccepted: false,
                    Outcome: TransitionOutcome.Rejected,
                    PreviousStatus: previousStatus,
                    NewStatus: CurrentStatus,
                    DidStatusChange: false,
                    DidConsumeReopenPermission: false,
                    ConsumedPermissionId: null,
                    RejectionReason: "Branch has already reported Completed. Administrator reopening permission is required to revert to Pending.");
            }

            if (!reopenPermission.TryConsume(
                    expectedBranchId: BranchId,
                    expectedActivityId: ActivityId,
                    currentReportingDate: currentReportingDate,
                    telegramUserId: telegramUserId,
                    telegramMessageId: telegramMessageLogId,
                    consumedAtUtc: occurredAtUtc,
                    out var failureReason))
            {
                return new StatusTransitionResult(
                    IsAccepted: false,
                    Outcome: TransitionOutcome.Rejected,
                    PreviousStatus: previousStatus,
                    NewStatus: CurrentStatus,
                    DidStatusChange: false,
                    DidConsumeReopenPermission: false,
                    ConsumedPermissionId: null,
                    RejectionReason: failureReason);
            }

            CurrentStatus = BranchActivityStatus.Pending;
            ReopenedCount++;
            LastTelegramMessageLogId = telegramMessageLogId;
            LastUpdatedByTelegramUserId = telegramUserId;
            LastUpdatedByDisplayName = senderDisplayName;
            UpdatedAtUtc = occurredAtUtc;

            return new StatusTransitionResult(
                IsAccepted: true,
                Outcome: TransitionOutcome.AcceptedViaReopen,
                PreviousStatus: previousStatus,
                NewStatus: CurrentStatus,
                DidStatusChange: true,
                DidConsumeReopenPermission: true,
                ConsumedPermissionId: reopenPermission.PermissionId,
                RejectionReason: null);
        }

        return new StatusTransitionResult(
            IsAccepted: false,
            Outcome: TransitionOutcome.Rejected,
            PreviousStatus: previousStatus,
            NewStatus: CurrentStatus,
            DidStatusChange: false,
            DidConsumeReopenPermission: false,
            ConsumedPermissionId: null,
            RejectionReason: $"Unsupported status transition from {previousStatus} to {incomingStatus}.");
    }
}

/// <summary>
/// Immutable audit history of every status submission and transition for a branch.
/// </summary>
public class ActivityHistory
{
    public long ActivityHistoryId { get; set; }
    public int ActivityId { get; set; }
    public int BranchId { get; set; }
    public DateOnly ReportingDate { get; set; }

    public BranchActivityStatus PreviousStatus { get; set; }
    public BranchActivityStatus NewStatus { get; set; }
    public TransitionOutcome Outcome { get; set; }

    public long? TelegramMessageLogId { get; set; }
    public long? ConsumedReopenPermissionId { get; set; }
    public long? TelegramUserId { get; set; }
    public string? TelegramUsername { get; set; }
    public string? TelegramDisplayName { get; set; }
    public string? Notes { get; set; }
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;

    public ActivityMaster Activity { get; set; } = null!;
    public BranchMaster Branch { get; set; } = null!;
    public StatusReopenPermission? ConsumedReopenPermission { get; set; }
}

/// <summary>
/// Single-use administrator permission enabling a Completed branch to transition back to Pending
/// for a specific activity and reporting date.
/// </summary>
public class StatusReopenPermission
{
    public long PermissionId { get; set; }
    public int BranchId { get; set; }
    public int ActivityId { get; set; }
    public DateOnly ReportingDate { get; set; }

    public string EnabledBy { get; set; } = string.Empty;
    public DateTime EnabledAtUtc { get; set; } = DateTime.UtcNow;
    public string Reason { get; set; } = string.Empty;

    public DateTime? ConsumedAtUtc { get; set; }
    public long? ConsumedByTelegramUserId { get; set; }
    public long? ConsumedMessageId { get; set; }

    public string? RevokedBy { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevocationReason { get; set; }

    public ReopenPermissionStatus PermissionStatus { get; set; } = ReopenPermissionStatus.Available;

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public BranchMaster Branch { get; set; } = null!;
    public ActivityMaster Activity { get; set; } = null!;

    public static StatusReopenPermission Grant(
        int branchId,
        int activityId,
        DateOnly reportingDate,
        string enabledBy,
        string reason,
        DateTime enabledAtUtc)
    {
        if (branchId <= 0)
            throw new ArgumentOutOfRangeException(nameof(branchId), "BranchId must be positive.");
        if (activityId <= 0)
            throw new ArgumentOutOfRangeException(nameof(activityId), "ActivityId must be positive.");
        if (string.IsNullOrWhiteSpace(enabledBy))
            throw new ArgumentException("Administrator identity (EnabledBy) is mandatory.", nameof(enabledBy));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A mandatory reason must be provided to grant reopening permission.", nameof(reason));

        return new StatusReopenPermission
        {
            BranchId = branchId,
            ActivityId = activityId,
            ReportingDate = reportingDate,
            EnabledBy = enabledBy.Trim(),
            Reason = reason.Trim(),
            EnabledAtUtc = enabledAtUtc,
            PermissionStatus = ReopenPermissionStatus.Available
        };
    }

    /// <summary>
    /// Revokes an Available reopening permission before it is consumed.
    /// Consumed or Expired permissions cannot be revoked, preserving single-use audit integrity.
    /// </summary>
    public void Revoke(string revokedBy, string reason, DateTime revokedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(revokedBy))
        {
            throw new ArgumentException("Administrator identity (revokedBy) is mandatory to revoke a reopening permission.", nameof(revokedBy));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A mandatory reason must be provided to revoke a reopening permission.", nameof(reason));
        }

        if (PermissionStatus == ReopenPermissionStatus.Consumed)
        {
            throw new InvalidOperationException(
                "Cannot revoke a reopening permission that has already been consumed.");
        }

        if (PermissionStatus == ReopenPermissionStatus.Revoked)
        {
            throw new InvalidOperationException(
                "Reopening permission has already been revoked.");
        }

        if (PermissionStatus == ReopenPermissionStatus.Expired)
        {
            throw new InvalidOperationException(
                "Cannot revoke an expired reopening permission.");
        }

        PermissionStatus = ReopenPermissionStatus.Revoked;
        RevokedBy = revokedBy.Trim();
        RevocationReason = reason.Trim();
        RevokedAtUtc = revokedAtUtc;
    }

    /// <summary>
    /// Expires an Available reopening permission if its granted ReportingDate is earlier than currentReportingDate.
    /// Consumed and Revoked permissions are never modified.
    /// </summary>
    public bool TryExpire(DateOnly currentReportingDate)
    {
        if (PermissionStatus == ReopenPermissionStatus.Available && currentReportingDate > ReportingDate)
        {
            PermissionStatus = ReopenPermissionStatus.Expired;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Batch helper that expires all Available permissions whose ReportingDate is earlier than currentReportingDate.
    /// </summary>
    public static int ExpireStalePermissions(
        IEnumerable<StatusReopenPermission> permissions,
        DateOnly currentReportingDate)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        int expiredCount = 0;
        foreach (var permission in permissions)
        {
            if (permission.TryExpire(currentReportingDate))
            {
                expiredCount++;
            }
        }

        return expiredCount;
    }

    public bool TryConsume(
        int expectedBranchId,
        int expectedActivityId,
        DateOnly currentReportingDate,
        long? telegramUserId,
        long telegramMessageId,
        DateTime consumedAtUtc,
        out string? failureReason)
    {
        if (BranchId != expectedBranchId || ActivityId != expectedActivityId)
        {
            failureReason = "Reopening permission does not match the branch or activity.";
            return false;
        }

        if (PermissionStatus == ReopenPermissionStatus.Consumed)
        {
            failureReason = "Reopening permission has already been consumed and cannot be reused.";
            return false;
        }

        if (PermissionStatus != ReopenPermissionStatus.Available)
        {
            failureReason = $"Reopening permission is not available (current status: {PermissionStatus}).";
            return false;
        }

        if (ReportingDate != currentReportingDate)
        {
            if (currentReportingDate > ReportingDate)
            {
                PermissionStatus = ReopenPermissionStatus.Expired;
            }

            failureReason = "Reopening permission is only valid for its granted reporting date and has expired.";
            return false;
        }

        PermissionStatus = ReopenPermissionStatus.Consumed;
        ConsumedAtUtc = consumedAtUtc;
        ConsumedByTelegramUserId = telegramUserId;
        ConsumedMessageId = telegramMessageId;
        failureReason = null;
        return true;
    }
}
