using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Reporting;

namespace OpsConnect.UnitTests;

public class CriticalInvariantsReviewTests
{
    private static readonly DateOnly TestDate = new(2026, 11, 7);

    // =========================================================================
    // REQUIREMENT 1: Reporting Cycle Reactivation & Reopen Permission Rules
    // =========================================================================

    [Fact]
    public void CompletedToPending_WithoutReopenPermission_ShouldBeRejected()
    {
        var status = new ActivityStatus
        {
            ActivityId = 1,
            BranchId = 101,
            ReportingDate = TestDate,
            CurrentStatus = BranchActivityStatus.Completed
        };

        var result = status.ApplyTransition(
            incomingStatus: BranchActivityStatus.Pending,
            currentReportingDate: TestDate,
            telegramMessageLogId: 5001,
            telegramUserId: 90001,
            senderDisplayName: "Branch User",
            occurredAtUtc: new DateTime(2026, 11, 7, 6, 0, 0, DateTimeKind.Utc),
            reopenPermission: null);

        Assert.False(result.IsAccepted);
        Assert.Equal(TransitionOutcome.Rejected, result.Outcome);
        Assert.Equal(BranchActivityStatus.Completed, status.CurrentStatus);
        Assert.False(result.DidConsumeReopenPermission);
    }

    [Theory]
    [InlineData("", "Valid reason")]
    [InlineData("   ", "Valid reason")]
    [InlineData("admin.user", "")]
    [InlineData("admin.user", "   ")]
    public void GrantReopenPermission_WithMissingAdminOrReason_ShouldThrow(string enabledBy, string reason)
    {
        Assert.Throws<ArgumentException>(() =>
            StatusReopenPermission.Grant(
                branchId: 101,
                activityId: 1,
                reportingDate: TestDate,
                enabledBy: enabledBy,
                reason: reason,
                enabledAtUtc: DateTime.UtcNow));
    }

    [Fact]
    public void CompletedToPending_WithValidPermission_ShouldConsumeOnceAndRejectSecondAttempt()
    {
        var permission = StatusReopenPermission.Grant(
            branchId: 101,
            activityId: 1,
            reportingDate: TestDate,
            enabledBy: "admin.ops",
            reason: "Branch submitted Completed prematurely",
            enabledAtUtc: new DateTime(2026, 11, 7, 6, 0, 0, DateTimeKind.Utc));
        permission.PermissionId = 77;

        var status = new ActivityStatus
        {
            ActivityId = 1,
            BranchId = 101,
            ReportingDate = TestDate,
            CurrentStatus = BranchActivityStatus.Completed
        };

        var consumedAtUtc = new DateTime(2026, 11, 7, 6, 5, 0, DateTimeKind.Utc);
        var firstAttempt = status.ApplyTransition(
            incomingStatus: BranchActivityStatus.Pending,
            currentReportingDate: TestDate,
            telegramMessageLogId: 5002,
            telegramUserId: 90002,
            senderDisplayName: "Branch Operator",
            occurredAtUtc: consumedAtUtc,
            reopenPermission: permission);

        Assert.True(firstAttempt.IsAccepted);
        Assert.Equal(TransitionOutcome.AcceptedViaReopen, firstAttempt.Outcome);
        Assert.True(firstAttempt.DidConsumeReopenPermission);
        Assert.Equal(77, firstAttempt.ConsumedPermissionId);
        Assert.Equal(BranchActivityStatus.Pending, status.CurrentStatus);
        Assert.Equal(1, status.ReopenedCount);
        Assert.Equal(ReopenPermissionStatus.Consumed, permission.PermissionStatus);
        Assert.Equal(consumedAtUtc, permission.ConsumedAtUtc);
        Assert.Equal(90002, permission.ConsumedByTelegramUserId);
        Assert.Equal(5002, permission.ConsumedMessageId);

        // Branch completes again, then attempts to reuse the same permission
        status.ApplyTransition(
            incomingStatus: BranchActivityStatus.Completed,
            currentReportingDate: TestDate,
            telegramMessageLogId: 5003,
            telegramUserId: 90002,
            senderDisplayName: "Branch Operator",
            occurredAtUtc: consumedAtUtc.AddMinutes(10));

        var secondReopenAttempt = status.ApplyTransition(
            incomingStatus: BranchActivityStatus.Pending,
            currentReportingDate: TestDate,
            telegramMessageLogId: 5004,
            telegramUserId: 90002,
            senderDisplayName: "Branch Operator",
            occurredAtUtc: consumedAtUtc.AddMinutes(15),
            reopenPermission: permission);

        Assert.False(secondReopenAttempt.IsAccepted);
        Assert.Equal(TransitionOutcome.Rejected, secondReopenAttempt.Outcome);
        Assert.Equal(BranchActivityStatus.Completed, status.CurrentStatus);
    }

    [Fact]
    public void ReopenPermission_FromPreviousReportingDate_ShouldExpireAndBeRejected()
    {
        var yesterday = TestDate.AddDays(-1);
        var permission = StatusReopenPermission.Grant(
            branchId: 101,
            activityId: 1,
            reportingDate: yesterday,
            enabledBy: "admin.ops",
            reason: "Yesterday permission",
            enabledAtUtc: new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc));

        var status = new ActivityStatus
        {
            ActivityId = 1,
            BranchId = 101,
            ReportingDate = TestDate,
            CurrentStatus = BranchActivityStatus.Completed
        };

        var result = status.ApplyTransition(
            incomingStatus: BranchActivityStatus.Pending,
            currentReportingDate: TestDate,
            telegramMessageLogId: 6001,
            telegramUserId: 90005,
            senderDisplayName: "Operator",
            occurredAtUtc: new DateTime(2026, 11, 7, 5, 0, 0, DateTimeKind.Utc),
            reopenPermission: permission);

        Assert.False(result.IsAccepted);
        Assert.Equal(ReopenPermissionStatus.Expired, permission.PermissionStatus);
        Assert.Equal(BranchActivityStatus.Completed, status.CurrentStatus);
    }

    [Fact]
    public void ReactivateAfterAuthorisedReopen_ShouldRestoreOnlyFutureSlotsAndKeepDeliveredSlotsIntact()
    {
        var cycle = new ReportingCycle
        {
            ReportingCycleId = 1,
            ActivityId = 1,
            ReportingDate = TestDate,
            CycleStatus = ReportingCycleStatus.Active
        };

        // 11:00 AM IST (05:30 UTC) already delivered
        var slot1100 = new ReportingSlot
        {
            ReportingSlotId = 1,
            ActivityId = 1,
            ReportingDate = TestDate,
            ScheduledSlotTime = new TimeOnly(11, 0),
            ScheduledExecutionUtc = new DateTime(2026, 11, 7, 5, 30, 0, DateTimeKind.Utc),
            SlotStatus = ReportDeliveryStatus.Delivered
        };

        // 11:30 AM IST (06:00 UTC) and 12:00 PM IST (06:30 UTC) pending when completion happens at 11:17 AM IST (05:47 UTC)
        var slot1130 = new ReportingSlot
        {
            ReportingSlotId = 2,
            ActivityId = 1,
            ReportingDate = TestDate,
            ScheduledSlotTime = new TimeOnly(11, 30),
            ScheduledExecutionUtc = new DateTime(2026, 11, 7, 6, 0, 0, DateTimeKind.Utc),
            SlotStatus = ReportDeliveryStatus.Pending
        };

        var slot1200 = new ReportingSlot
        {
            ReportingSlotId = 3,
            ActivityId = 1,
            ReportingDate = TestDate,
            ScheduledSlotTime = new TimeOnly(12, 0),
            ScheduledExecutionUtc = new DateTime(2026, 11, 7, 6, 30, 0, DateTimeKind.Utc),
            SlotStatus = ReportDeliveryStatus.Pending
        };

        cycle.Slots.Add(slot1100);
        cycle.Slots.Add(slot1130);
        cycle.Slots.Add(slot1200);

        // Complete at 11:17 AM IST (05:47 UTC)
        var completedAtUtc = new DateTime(2026, 11, 7, 5, 47, 0, DateTimeKind.Utc);
        Assert.True(cycle.TryCompleteWhenAllBranchesCompleted(10, 10, completedAtUtc));
        Assert.Equal(ReportDeliveryStatus.Delivered, slot1100.SlotStatus);
        Assert.Equal(ReportDeliveryStatus.SkippedAllCompleted, slot1130.SlotStatus);
        Assert.Equal(ReportDeliveryStatus.SkippedAllCompleted, slot1200.SlotStatus);

        // Admin grants reopen permission and branch consumes it at 11:40 AM IST (06:10 UTC) — after 11:30 slot, before 12:00 slot
        var permission = StatusReopenPermission.Grant(
            branchId: 101,
            activityId: 1,
            reportingDate: TestDate,
            enabledBy: "admin.ops",
            reason: "Reopen branch 36:01 for recount",
            enabledAtUtc: new DateTime(2026, 11, 7, 6, 5, 0, DateTimeKind.Utc));
        permission.PermissionId = 88;

        var reactivatedAtUtc = new DateTime(2026, 11, 7, 6, 10, 0, DateTimeKind.Utc);
        Assert.True(permission.TryConsume(101, 1, TestDate, 90010, 7001, reactivatedAtUtc, out _));

        var reactivated = cycle.ReactivateAfterAuthorisedReopen(permission, reactivatedAtUtc);

        Assert.True(reactivated);
        Assert.Equal(ReportingCycleStatus.Active, cycle.CycleStatus);
        Assert.Equal(ReportingCycleStopReason.None, cycle.StopReason);
        Assert.Null(cycle.CompletedAtUtc);
        Assert.Equal(reactivatedAtUtc, cycle.LastReactivatedAtUtc);
        Assert.Equal(101, cycle.LastReactivatedByBranchId);
        Assert.Equal(88, cycle.LastReactivatedByPermissionId);
        Assert.Equal("Reopen branch 36:01 for recount", cycle.LastReactivationReason);

        // 11:00 slot stays Delivered; 11:30 slot (already in the past at 11:40) stays SkippedAllCompleted;
        // 12:00 future slot resumes as Pending; no invented slots are created.
        Assert.Equal(3, cycle.Slots.Count);
        Assert.Equal(ReportDeliveryStatus.Delivered, slot1100.SlotStatus);
        Assert.Equal(ReportDeliveryStatus.SkippedAllCompleted, slot1130.SlotStatus);
        Assert.Equal(ReportDeliveryStatus.Pending, slot1200.SlotStatus);
    }

    // =========================================================================
    // REQUIREMENT 2: Immediate Final Report When All Branches Complete
    // =========================================================================

    [Fact]
    public void TryCompleteWhenAllBranchesCompleted_WithEmptyBranchPopulation_ShouldReturnFalse()
    {
        var cycle = new ReportingCycle
        {
            ActivityId = 1,
            ReportingDate = TestDate
        };

        var triggered = cycle.TryCompleteWhenAllBranchesCompleted(
            totalActiveBranches: 0,
            completedBranches: 0,
            completedAtUtc: DateTime.UtcNow);

        Assert.False(triggered);
        Assert.Equal(ReportingCycleStatus.Active, cycle.CycleStatus);
        Assert.Equal(0, cycle.CompletionSequenceNumber);
    }

    [Theory]
    [InlineData(10, 9)]
    [InlineData(10, 0)]
    public void TryCompleteWhenAllBranchesCompleted_WithPendingOrNotReportedBranch_ShouldReturnFalse(
        int totalActive,
        int completed)
    {
        var cycle = new ReportingCycle
        {
            ActivityId = 1,
            ReportingDate = TestDate
        };

        var triggered = cycle.TryCompleteWhenAllBranchesCompleted(
            totalActiveBranches: totalActive,
            completedBranches: completed,
            completedAtUtc: DateTime.UtcNow);

        Assert.False(triggered);
        Assert.Equal(ReportingCycleStatus.Active, cycle.CycleStatus);
        Assert.Equal(0, cycle.CompletionSequenceNumber);
    }

    [Fact]
    public void TryCompleteWhenAllBranchesCompleted_ShouldTriggerExactlyOncePerCompletionCycle()
    {
        var cycle = new ReportingCycle
        {
            ActivityId = 1,
            ReportingDate = TestDate
        };

        var completedAtUtc = new DateTime(2026, 11, 7, 5, 47, 0, DateTimeKind.Utc);
        var firstTrigger = cycle.TryCompleteWhenAllBranchesCompleted(50, 50, completedAtUtc);
        var duplicateTrigger = cycle.TryCompleteWhenAllBranchesCompleted(50, 50, completedAtUtc.AddSeconds(2));

        Assert.True(firstTrigger);
        Assert.False(duplicateTrigger);
        Assert.Equal(ReportingCycleStatus.Completed, cycle.CycleStatus);
        Assert.Equal(ReportingCycleStopReason.AllBranchesCompleted, cycle.StopReason);
        Assert.Equal(1, cycle.CompletionSequenceNumber);

        var finalReportKey = SummaryLog.BuildImmediateFinalIdempotencyKey(
            cycle.ActivityId,
            cycle.ReportingDate,
            cycle.CompletionSequenceNumber);

        Assert.Equal("FINAL:1:2026-11-07:SEQ:1", finalReportKey);
    }

    // =========================================================================
    // REQUIREMENT 3: Delivery Idempotency & Safe Retry Handling
    // =========================================================================

    [Fact]
    public void ReportDeliveryLog_WhenDeliveryFailsAndRetries_ShouldKeepSameReportAndPreventDuplicateDelivery()
    {
        var reportKey = SummaryLog.BuildImmediateFinalIdempotencyKey(1, TestDate, completionSequenceNumber: 1);
        var deliveryKey = ReportDeliveryLog.BuildDeliveryIdempotencyKey(reportKey, destinationGroupId: 5);

        var delivery = new ReportDeliveryLog
        {
            ReportDeliveryId = 10,
            ReportId = 200,
            ActivityId = 1,
            ReportingDate = TestDate,
            DestinationGroupId = 5,
            DestinationTelegramChatId = -1009988776655,
            IdempotencyKey = deliveryKey,
            DeliveryStatus = ReportDeliveryStatus.DeliveryPending
        };

        Assert.Equal("FINAL:1:2026-11-07:SEQ:1:DEST:5", delivery.IdempotencyKey);
        Assert.True(delivery.CanAttemptDelivery());

        // Attempt 1 fails due to transient Telegram timeout
        var firstAttemptUtc = new DateTime(2026, 11, 7, 5, 47, 5, DateTimeKind.Utc);
        delivery.RecordFailedAttempt("HTTP 504 Gateway Timeout from Telegram API", firstAttemptUtc);

        Assert.Equal(ReportDeliveryStatus.Failed, delivery.DeliveryStatus);
        Assert.Equal(1, delivery.DeliveryAttempts);
        Assert.Null(delivery.DeliveredAtUtc);
        Assert.Null(delivery.DeliveredTelegramMessageIds);
        Assert.True(delivery.CanAttemptDelivery());

        // Attempt 2 succeeds on the exact same ReportDeliveryLog (ReportId = 200)
        var secondAttemptUtc = firstAttemptUtc.AddSeconds(30);
        delivery.MarkDelivered("987654", secondAttemptUtc);

        Assert.Equal(ReportDeliveryStatus.Delivered, delivery.DeliveryStatus);
        Assert.Equal(2, delivery.DeliveryAttempts);
        Assert.Equal("987654", delivery.DeliveredTelegramMessageIds);
        Assert.Equal(secondAttemptUtc, delivery.DeliveredAtUtc);
        Assert.Null(delivery.LastError);
        Assert.False(delivery.CanAttemptDelivery());

        // Subsequent duplicate delivery or failure calls must be rejected
        Assert.Throws<InvalidOperationException>(() =>
            delivery.MarkDelivered("987655", secondAttemptUtc.AddSeconds(5)));

        Assert.Throws<InvalidOperationException>(() =>
            delivery.RecordFailedAttempt("Late error", secondAttemptUtc.AddSeconds(5)));
    }
}
