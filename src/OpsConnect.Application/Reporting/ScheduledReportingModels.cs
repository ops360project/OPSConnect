using OpsConnect.Domain.Reporting;

namespace OpsConnect.Application.Reporting;

public sealed record ScheduledReportingDispatchResult(
    DateTime DispatchTimestampUtc,
    int ActivitiesEvaluated,
    int CyclesEnsured,
    int SlotsEvaluated,
    int SlotsClaimedAndProcessed,
    int SlotsSkipped,
    int SlotsFailed,
    IReadOnlyList<SlotExecutionResult> ExecutedSlots);

public sealed record SlotExecutionResult(
    long ReportingSlotId,
    int ActivityId,
    string ActivityCode,
    DateOnly ReportingDate,
    TimeOnly ScheduledSlotTime,
    ReportDeliveryStatus Status,
    int TotalBranches,
    int CompletedBranches,
    decimal CompletionPercentage,
    int DestinationGroupsTargeted,
    int DestinationGroupsDelivered,
    int MessagePartsSent,
    bool IsIdempotentRetry,
    string? StatusNotes,
    string? ErrorMessage);

public sealed record ScheduledReportingCycleStatusDto(
    long ReportingCycleId,
    int ActivityId,
    string ActivityCode,
    DateOnly ReportingDate,
    ReportingCycleStatus CycleStatus,
    ReportingCycleStopReason StopReason,
    int TotalSlots,
    int PendingSlots,
    int ProcessingSlots,
    int DeliveredSlots,
    int FailedSlots,
    int SkippedSlots,
    DateTime? CompletedAtUtc,
    int CompletionSequenceNumber = 0,
    DateTime? LastReactivatedAtUtc = null,
    int? LastReactivatedByBranchId = null);

public sealed record ImmediateFinalReportResult(
    bool IsTriggered,
    long? ReportId,
    int ActivityId,
    string ActivityCode,
    DateOnly ReportingDate,
    int CompletionSequenceNumber,
    ReportingCycleStatus CycleStatus,
    ReportDeliveryStatus DeliveryStatus,
    int TotalActiveBranches,
    int CompletedBranches,
    int DestinationGroupsTargeted,
    int DestinationGroupsDelivered,
    int MessagePartsSent,
    bool IsIdempotentRetry,
    string? StatusNotes,
    string? ErrorMessage);

public sealed record ScheduledSlotStatusDto(
    long ReportingSlotId,
    int ActivityId,
    string ActivityCode,
    DateOnly ReportingDate,
    TimeOnly ScheduledSlotTime,
    DateTime ScheduledExecutionUtc,
    ReportDeliveryStatus SlotStatus,
    DateTime? ExecutedAtUtc,
    string? StatusNotes,
    long? ReportId,
    IReadOnlyList<ScheduledDeliveryStatusDto> Deliveries);

public sealed record ScheduledDeliveryStatusDto(
    long ReportDeliveryId,
    int DestinationGroupId,
    long DestinationTelegramChatId,
    ReportDeliveryStatus DeliveryStatus,
    int DeliveryAttempts,
    string? DeliveredTelegramMessageIds,
    DateTime? DeliveredAtUtc,
    string? LastError);
