using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Common;
using OpsConnect.Domain.Organisation;
using OpsConnect.Domain.Telegram;

namespace OpsConnect.Domain.Reporting;

public enum ReportingCycleStatus
{
    Active = 1,
    Completed = 2,
    StoppedManually = 3
}

public enum ReportingCycleStopReason
{
    None = 0,
    AllBranchesCompleted = 1,
    EndOfSchedule = 2,
    DisabledByAdmin = 3
}

public enum ReportingTriggerType
{
    ScheduledSlot = 1,
    ImmediateFinalAllCompleted = 2,
    ManualAdminTrigger = 3
}

public enum ReportDeliveryStatus
{
    Pending = 1,
    Processing = 2,
    Generated = 3,
    DeliveryPending = 4,
    Delivered = 5,
    Failed = 6,
    SkippedAllCompleted = 7,
    CancelledByConfiguration = 8
}

/// <summary>
/// Tracks the daily reporting lifecycle for an activity, including automatic stop when
/// all branches complete and reactivation when an authorised reopening permission is consumed.
/// </summary>
public class ReportingCycle : AuditableEntity
{
    public long ReportingCycleId { get; set; }
    public int ActivityId { get; set; }
    public DateOnly ReportingDate { get; set; }

    public ReportingCycleStatus CycleStatus { get; set; } = ReportingCycleStatus.Active;
    public ReportingCycleStopReason StopReason { get; set; } = ReportingCycleStopReason.None;

    public DateTime? CompletedAtUtc { get; set; }
    public int CompletionSequenceNumber { get; set; }

    public DateTime? LastReactivatedAtUtc { get; set; }
    public int? LastReactivatedByBranchId { get; set; }
    public long? LastReactivatedByPermissionId { get; set; }
    public string? LastReactivationReason { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public ActivityMaster Activity { get; set; } = null!;
    public BranchMaster? LastReactivatedByBranch { get; set; }
    public StatusReopenPermission? LastReactivatedByPermission { get; set; }
    public ICollection<ReportingSlot> Slots { get; set; } = new List<ReportingSlot>();
    public ICollection<SummaryLog> Summaries { get; set; } = new List<SummaryLog>();

    /// <summary>
    /// Evaluates whether all active mapped branches have completed the activity.
    /// Enforces the non-empty branch population rule and idempotently transitions Active -> Completed,
    /// incrementing CompletionSequenceNumber and marking future pending slots as SkippedAllCompleted.
    /// </summary>
    public bool TryCompleteWhenAllBranchesCompleted(
        int totalActiveBranches,
        int completedBranches,
        DateTime completedAtUtc)
    {
        // Rule (Section 13): An empty branch population must never trigger the automatic final-report condition.
        if (totalActiveBranches <= 0)
        {
            return false;
        }

        // Any Pending or NotReported branch prevents completion.
        if (completedBranches != totalActiveBranches)
        {
            return false;
        }

        // Idempotency guard: if already completed, do not trigger another final report.
        if (CycleStatus != ReportingCycleStatus.Active)
        {
            return false;
        }

        CycleStatus = ReportingCycleStatus.Completed;
        StopReason = ReportingCycleStopReason.AllBranchesCompleted;
        CompletedAtUtc = completedAtUtc;
        CompletionSequenceNumber++;
        UpdatedAtUtc = completedAtUtc;

        // Mark all subsequent Pending slots as SkippedAllCompleted (never Delivered).
        foreach (var slot in Slots)
        {
            if (slot.SlotStatus == ReportDeliveryStatus.Pending &&
                slot.ScheduledExecutionUtc >= completedAtUtc)
            {
                slot.SlotStatus = ReportDeliveryStatus.SkippedAllCompleted;
                slot.StatusNotes = "Skipped automatically because all branches completed.";
                slot.UpdatedAtUtc = completedAtUtc;
            }
        }

        return true;
    }

    /// <summary>
    /// Reactivates a completed reporting cycle after an authorised reopening permission is consumed
    /// by a valid Pending update. Restores only future configured slots without resending delivered reports
    /// or inventing new reporting slots.
    /// </summary>
    public bool ReactivateAfterAuthorisedReopen(
        StatusReopenPermission consumedPermission,
        DateTime reactivatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(consumedPermission);

        if (consumedPermission.PermissionStatus != ReopenPermissionStatus.Consumed)
        {
            throw new InvalidOperationException(
                "Reporting cycle can only be reactivated by a consumed reopening permission.");
        }

        if (consumedPermission.ActivityId != ActivityId || consumedPermission.ReportingDate != ReportingDate)
        {
            throw new InvalidOperationException(
                "Consumed reopening permission does not belong to this activity and reporting date.");
        }

        if (CycleStatus != ReportingCycleStatus.Completed)
        {
            return false;
        }

        CycleStatus = ReportingCycleStatus.Active;
        StopReason = ReportingCycleStopReason.None;
        CompletedAtUtc = null;
        LastReactivatedAtUtc = reactivatedAtUtc;
        LastReactivatedByBranchId = consumedPermission.BranchId;
        LastReactivatedByPermissionId = consumedPermission.PermissionId;
        LastReactivationReason = consumedPermission.Reason;
        UpdatedAtUtc = reactivatedAtUtc;

        // Resume only future configured slots that were previously marked SkippedAllCompleted.
        // Never touch Delivered slots and never invent a slot if no future slot remains.
        foreach (var slot in Slots)
        {
            if (slot.SlotStatus == ReportDeliveryStatus.SkippedAllCompleted &&
                slot.ScheduledExecutionUtc > reactivatedAtUtc)
            {
                slot.SlotStatus = ReportDeliveryStatus.Pending;
                slot.StatusNotes = $"Resumed after authorised reopen of branch ID {consumedPermission.BranchId}.";
                slot.UpdatedAtUtc = reactivatedAtUtc;
            }
        }

        return true;
    }
}

/// <summary>
/// Represents a durable scheduled reporting slot for an activity on a specific reporting date.
/// </summary>
public class ReportingSlot : AuditableEntity
{
    public long ReportingSlotId { get; set; }
    public long ReportingCycleId { get; set; }
    public int ActivityId { get; set; }
    public DateOnly ReportingDate { get; set; }

    public TimeOnly ScheduledSlotTime { get; set; }
    public DateTime ScheduledExecutionUtc { get; set; }

    public ReportDeliveryStatus SlotStatus { get; set; } = ReportDeliveryStatus.Pending;
    public string? HangfireJobId { get; set; }
    public DateTime? ExecutedAtUtc { get; set; }
    public string? StatusNotes { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public ReportingCycle ReportingCycle { get; set; } = null!;
    public ActivityMaster Activity { get; set; } = null!;
}

/// <summary>
/// Stores a generated summary report snapshot before delivery to one or more Telegram destinations.
/// </summary>
public class SummaryLog
{
    public long ReportId { get; set; }
    public long ReportingCycleId { get; set; }
    public int ActivityId { get; set; }
    public DateOnly ReportingDate { get; set; }

    public ReportingTriggerType TriggerType { get; set; }
    public TimeOnly? ScheduledSlotTime { get; set; }

    /// <summary>
    /// Unique idempotency key preventing duplicate report generation for the same slot or final-report cycle.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public int TotalActiveBranches { get; set; }
    public int CompletedBranches { get; set; }
    public int PendingBranches { get; set; }
    public int NotReportedBranches { get; set; }
    public decimal CompletionPercentage { get; set; }

    public string FormattedReportContent { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string? TriggeredBy { get; set; }

    public ReportingCycle ReportingCycle { get; set; } = null!;
    public ActivityMaster Activity { get; set; } = null!;
    public ICollection<ReportDeliveryLog> Deliveries { get; set; } = new List<ReportDeliveryLog>();

    public static string BuildScheduledSlotIdempotencyKey(
        int activityId,
        DateOnly reportingDate,
        TimeOnly scheduledSlotTime) =>
        $"SLOT:{activityId}:{reportingDate:yyyy-MM-dd}:{scheduledSlotTime:HHmm}";

    public static string BuildImmediateFinalIdempotencyKey(
        int activityId,
        DateOnly reportingDate,
        int completionSequenceNumber)
    {
        if (completionSequenceNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completionSequenceNumber),
                "CompletionSequenceNumber must be positive for an immediate final report.");
        }

        return $"FINAL:{activityId}:{reportingDate:yyyy-MM-dd}:SEQ:{completionSequenceNumber}";
    }
}

/// <summary>
/// Tracks independent delivery status, retries, and Telegram confirmation for each destination group.
/// </summary>
public class ReportDeliveryLog : AuditableEntity
{
    public long ReportDeliveryId { get; set; }
    public long ReportId { get; set; }
    public int ActivityId { get; set; }
    public DateOnly ReportingDate { get; set; }
    public TimeOnly? ScheduledSlotTime { get; set; }

    public int DestinationGroupId { get; set; }
    public long DestinationTelegramChatId { get; set; }

    /// <summary>
    /// Unique idempotency key: ActivityId + ReportingDate + ScheduledSlot/FinalCycleKey + DestinationGroupId.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public ReportDeliveryStatus DeliveryStatus { get; set; } = ReportDeliveryStatus.DeliveryPending;
    public int DeliveryAttempts { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// Primary Telegram message ID (or comma-separated IDs for multi-part split reports) upon confirmed delivery.
    /// </summary>
    public string? DeliveredTelegramMessageIds { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public SummaryLog SummaryReport { get; set; } = null!;
    public TelegramGroup DestinationGroup { get; set; } = null!;

    public static string BuildDeliveryIdempotencyKey(string reportIdempotencyKey, int destinationGroupId)
    {
        if (string.IsNullOrWhiteSpace(reportIdempotencyKey))
            throw new ArgumentException("Report idempotency key is required.", nameof(reportIdempotencyKey));
        if (destinationGroupId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationGroupId), "DestinationGroupId must be positive.");

        return $"{reportIdempotencyKey}:DEST:{destinationGroupId}";
    }

    public bool CanAttemptDelivery() =>
        DeliveryStatus is ReportDeliveryStatus.Pending
            or ReportDeliveryStatus.DeliveryPending
            or ReportDeliveryStatus.Failed;

    public void RecordFailedAttempt(string errorMessage, DateTime attemptedAtUtc)
    {
        if (DeliveryStatus == ReportDeliveryStatus.Delivered)
        {
            throw new InvalidOperationException(
                "Cannot mark a report delivery as failed after Telegram delivery has already been confirmed.");
        }

        DeliveryAttempts++;
        DeliveryStatus = ReportDeliveryStatus.Failed;
        LastError = string.IsNullOrWhiteSpace(errorMessage) ? "Unknown Telegram delivery error." : errorMessage.Trim();
        UpdatedAtUtc = attemptedAtUtc;
    }

    public void MarkDelivered(string deliveredTelegramMessageIds, DateTime deliveredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(deliveredTelegramMessageIds))
        {
            throw new ArgumentException(
                "A report cannot be marked Delivered without a confirmed Telegram message ID.",
                nameof(deliveredTelegramMessageIds));
        }

        if (DeliveryStatus == ReportDeliveryStatus.Delivered)
        {
            throw new InvalidOperationException(
                "Report delivery is already marked Delivered; duplicate delivery confirmation is rejected.");
        }

        DeliveryAttempts++;
        DeliveryStatus = ReportDeliveryStatus.Delivered;
        DeliveredTelegramMessageIds = deliveredTelegramMessageIds.Trim();
        DeliveredAtUtc = deliveredAtUtc;
        LastError = null;
        UpdatedAtUtc = deliveredAtUtc;
    }
}
