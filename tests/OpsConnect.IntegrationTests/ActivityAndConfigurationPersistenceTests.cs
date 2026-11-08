using Microsoft.EntityFrameworkCore;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Administration;
using OpsConnect.Application.Common;
using OpsConnect.Application.Organisation;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Reporting;
using OpsConnect.Domain.Telegram;
using OpsConnect.Infrastructure.Persistence;
using OpsConnect.Infrastructure.Services;

namespace OpsConnect.IntegrationTests;

public class ActivityAndConfigurationPersistenceTests
{
    private static readonly DateOnly Day1 = new(2026, 11, 7);
    private static readonly DateOnly Day2 = new(2026, 11, 8);

    private static OpsConnectDbContext CreateContext()
    {
        var factory = new OpsConnectDbContextFactory();
        return factory.CreateDbContext(Array.Empty<string>());
    }

    private static (
        OrganisationMasterService OrgService,
        RffoActivityUpdateHandler RffoHandler,
        IActivityUpdateProcessor Processor,
        ActivityStatusQueryService QueryService,
        StatusReopenPermissionService ReopenService,
        ActivityConfigurationService ConfigService) CreateServices(OpsConnectDbContext context)
    {
        var clock = new SystemBusinessClock();
        var orgService = new OrganisationMasterService(
            context,
            new OrganisationExcelReader(),
            new OrganisationHierarchyImportValidator(),
            clock);
        var rffoHandler = new RffoActivityUpdateHandler(context, clock);
        var processor = new ActivityUpdateProcessor(
            new TelegramActivityMessageParser(),
            new IActivityUpdateHandler[] { rffoHandler });
        var queryService = new ActivityStatusQueryService(context, clock);
        var reopenService = new StatusReopenPermissionService(context, clock);
        var configService = new ActivityConfigurationService(context, clock);

        return (orgService, rffoHandler, processor, queryService, reopenService, configService);
    }

    private static OrganisationImportRow[] CreateTestHierarchyBatch() =>
    [
        new OrganisationImportRow(
            RowNumber: 2,
            ZoneCode: "EAST",
            ZoneName: "East Zone",
            StateCode: "OD",
            StateName: "Odisha",
            RegionCode: "REG-A",
            RegionName: "Region A",
            BranchCode: "36:01",
            BranchName: "Bhubaneswar Main",
            IsZoneActive: true,
            IsStateActive: true,
            IsRegionActive: true,
            IsBranchActive: true),
        new OrganisationImportRow(
            RowNumber: 3,
            ZoneCode: "EAST",
            ZoneName: "East Zone",
            StateCode: "OD",
            StateName: "Odisha",
            RegionCode: "REG-A",
            RegionName: "Region A",
            BranchCode: "36:02",
            BranchName: "Cuttack City",
            IsZoneActive: true,
            IsStateActive: true,
            IsRegionActive: true,
            IsBranchActive: true),
        new OrganisationImportRow(
            RowNumber: 4,
            ZoneCode: "EAST",
            ZoneName: "East Zone",
            StateCode: "OD",
            StateName: "Odisha",
            RegionCode: "REG-B",
            RegionName: "Region B",
            BranchCode: "36:03",
            BranchName: "Inactive Rourkela Branch",
            IsZoneActive: true,
            IsStateActive: true,
            IsRegionActive: true,
            IsBranchActive: false)
    ];

    // =========================================================================
    // 1. RFFO Status Transitions, Daily Isolation & Derived NotReported Tests
    // =========================================================================

    [Fact]
    public async Task RffoTransitions_LifecycleAndDerivedNotReported_ShouldEnforceAllTransitionRulesAndRecordHistory()
    {
        await using var context = CreateContext();
        if (!await context.Database.CanConnectAsync())
        {
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync();
            var (orgService, _, processor, queryService, _, _) = CreateServices(context);

            await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: new DateOnly(2026, 11, 1));

            // 1. Initial daily snapshot on Day 1: both active branches (36:01 and 36:02) start as derived NotReported
            var initialSnapshot = await queryService.GetBranchStatusesAsync(
                new ActivityStatusFilterQuery(ReportingDate: Day1));

            Assert.Equal(2, initialSnapshot.TotalActiveBranches);
            Assert.Equal(0, initialSnapshot.CompletedBranches);
            Assert.Equal(0, initialSnapshot.PendingBranches);
            Assert.Equal(2, initialSnapshot.NotReportedBranches);
            Assert.Equal(0m, initialSnapshot.CompletionPercentage);
            Assert.All(initialSnapshot.Branches, b =>
            {
                Assert.Equal(BranchActivityStatus.NotReported, b.CurrentStatus);
                Assert.True(b.IsDerivedNotReported);
            });

            // 2. Branch 36:01 -> NotReported to Pending (Accepted)
            var pending3601 = await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                ActivityCode: "RFFO",
                BranchCode: "36:01",
                IncomingStatus: BranchActivityStatus.Pending,
                TelegramMessageLogId: 1001,
                TelegramUserId: 7001,
                SenderUsername: "op_3601",
                SenderDisplayName: "Operator 36:01",
                ExplicitReportingDate: Day1,
                PendingReason: PendingReasonCode.CashShortage));

            Assert.True(pending3601.IsAccepted);
            Assert.Equal(MessageProcessingStatus.Accepted, pending3601.ProcessingStatus);
            Assert.Equal(TransitionOutcome.Accepted, pending3601.Outcome);
            Assert.Equal(BranchActivityStatus.NotReported, pending3601.PreviousStatus);
            Assert.Equal(BranchActivityStatus.Pending, pending3601.CurrentStatus);
            Assert.True(pending3601.DidStatusChange);
            Assert.False(pending3601.DidTriggerImmediateFinalReportCondition);

            // 3. Branch 36:01 -> Pending to Pending (AcceptedUnchanged; no duplicate ActivityStatus row)
            var duplicatePending3601 = await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                ActivityCode: "RFFO",
                BranchCode: "36:01",
                IncomingStatus: BranchActivityStatus.Pending,
                TelegramMessageLogId: 1002,
                TelegramUserId: 7001,
                SenderDisplayName: "Operator 36:01",
                ExplicitReportingDate: Day1,
                PendingReason: PendingReasonCode.CashShortage));

            Assert.True(duplicatePending3601.IsAccepted);
            Assert.Equal(MessageProcessingStatus.AcceptedUnchanged, duplicatePending3601.ProcessingStatus);
            Assert.Equal(TransitionOutcome.AcceptedUnchanged, duplicatePending3601.Outcome);
            Assert.False(duplicatePending3601.DidStatusChange);
            Assert.Equal(1, await context.ActivityStatuses.CountAsync(s => s.ReportingDate == Day1));

            // 4. Branch 36:01 -> Pending to Completed (Accepted; 36:02 still NotReported so final condition is false)
            var completed3601 = await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                ActivityCode: "RFFO",
                BranchCode: "36:01",
                IncomingStatus: BranchActivityStatus.Completed,
                TelegramMessageLogId: 1003,
                TelegramUserId: 7001,
                SenderDisplayName: "Operator 36:01",
                ExplicitReportingDate: Day1,
                PendingReason: PendingReasonCode.CashShortage));

            Assert.True(completed3601.IsAccepted);
            Assert.Equal(TransitionOutcome.Accepted, completed3601.Outcome);
            Assert.Equal(BranchActivityStatus.Pending, completed3601.PreviousStatus);
            Assert.Equal(BranchActivityStatus.Completed, completed3601.CurrentStatus);
            Assert.False(completed3601.DidTriggerImmediateFinalReportCondition);

            // 5. Branch 36:01 -> Completed to Completed (AcceptedUnchanged)
            var duplicateCompleted3601 = await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                ActivityCode: "RFFO",
                BranchCode: "36:01",
                IncomingStatus: BranchActivityStatus.Completed,
                TelegramMessageLogId: 1004,
                TelegramUserId: 7001,
                SenderDisplayName: "Operator 36:01",
                ExplicitReportingDate: Day1,
                PendingReason: PendingReasonCode.CashShortage));

            Assert.True(duplicateCompleted3601.IsAccepted);
            Assert.Equal(TransitionOutcome.AcceptedUnchanged, duplicateCompleted3601.Outcome);
            Assert.False(duplicateCompleted3601.DidStatusChange);

            // 6. Branch 36:01 -> Completed to Pending without permission (Rejected & recorded in ActivityHistory)
            var rejectedRevert3601 = await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                ActivityCode: "RFFO",
                BranchCode: "36:01",
                IncomingStatus: BranchActivityStatus.Pending,
                TelegramMessageLogId: 1005,
                TelegramUserId: 7001,
                SenderDisplayName: "Operator 36:01",
                ExplicitReportingDate: Day1,
                PendingReason: PendingReasonCode.CashShortage));

            Assert.False(rejectedRevert3601.IsAccepted);
            Assert.Equal(MessageProcessingStatus.RejectedInvalidTransition, rejectedRevert3601.ProcessingStatus);
            Assert.Equal(TransitionOutcome.Rejected, rejectedRevert3601.Outcome);
            Assert.Equal(BranchActivityStatus.Completed, rejectedRevert3601.CurrentStatus);

            // 7. Branch 36:02 -> Direct NotReported to Completed (Accepted; all 2 active branches now Completed -> triggers final condition!)
            var completed3602 = await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                ActivityCode: "RFFO",
                BranchCode: "36:02",
                IncomingStatus: BranchActivityStatus.Completed,
                TelegramMessageLogId: 1006,
                TelegramUserId: 7002,
                SenderDisplayName: "Operator 36:02",
                ExplicitReportingDate: Day1,
                PendingReason: PendingReasonCode.CashShortage));

            Assert.True(completed3602.IsAccepted);
            Assert.Equal(BranchActivityStatus.NotReported, completed3602.PreviousStatus);
            Assert.Equal(BranchActivityStatus.Completed, completed3602.CurrentStatus);
            Assert.True(completed3602.DidTriggerImmediateFinalReportCondition);

            // Verify Day 1 history for 36:01 has all 5 transitions (including AcceptedUnchanged and Rejected)
            var history3601 = await queryService.GetBranchActivityHistoryAsync("RFFO", "36:01", Day1);
            Assert.NotNull(history3601);
            Assert.Equal(BranchActivityStatus.Completed, history3601!.CurrentStatusOnDate);
            Assert.Equal(5, history3601.HistoryEntries.Count);
            Assert.Contains(history3601.HistoryEntries, h => h.Outcome == TransitionOutcome.Rejected && h.TelegramMessageLogId == 1005);

            // 8. Next day (Day 2): yesterday's Completed status never carries over; both branches start Day 2 at NotReported
            var day2Snapshot = await queryService.GetBranchStatusesAsync(
                new ActivityStatusFilterQuery(ReportingDate: Day2));

            Assert.Equal(2, day2Snapshot.TotalActiveBranches);
            Assert.Equal(2, day2Snapshot.NotReportedBranches);
            Assert.Equal(0, day2Snapshot.CompletedBranches);

            await tx.RollbackAsync();
        });
    }

    // =========================================================================
    // 2. Unknown/Inactive Branch, Invalid Activity & Duplicate Source Message
    // =========================================================================

    [Fact]
    public async Task RffoProcessing_InvalidBranchInactiveBranchInvalidActivityAndDuplicateSourceMessage_ShouldRejectProperly()
    {
        await using var context = CreateContext();
        if (!await context.Database.CanConnectAsync())
        {
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync();
            var (orgService, _, processor, _, _, configService) = CreateServices(context);

            await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: new DateOnly(2026, 11, 1));

            // 1. Unknown branch 99:99 -> RejectedUnknownBranch
            var unknownBranch = await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                ActivityCode: "RFFO",
                BranchCode: "99:99",
                IncomingStatus: BranchActivityStatus.Pending,
                TelegramMessageLogId: 2001,
                ExplicitReportingDate: Day1,
                PendingReason: PendingReasonCode.CashShortage));

            Assert.False(unknownBranch.IsAccepted);
            Assert.Equal(MessageProcessingStatus.RejectedUnknownBranch, unknownBranch.ProcessingStatus);

            // 2. Inactive branch 36:03 -> RejectedInactiveBranch
            var inactiveBranch = await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                ActivityCode: "RFFO",
                BranchCode: "36:03",
                IncomingStatus: BranchActivityStatus.Pending,
                TelegramMessageLogId: 2002,
                ExplicitReportingDate: Day1,
                PendingReason: PendingReasonCode.CashShortage));

            Assert.False(inactiveBranch.IsAccepted);
            Assert.Equal(MessageProcessingStatus.RejectedInactiveBranch, inactiveBranch.ProcessingStatus);

            // 3. Valid submission with TelegramMessageLogId = 2003, followed by duplicate submission with same TelegramMessageLogId = 2003
            var firstSubmission = await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                ActivityCode: "RFFO",
                BranchCode: "36:01",
                IncomingStatus: BranchActivityStatus.Pending,
                TelegramMessageLogId: 2003,
                ExplicitReportingDate: Day1,
                PendingReason: PendingReasonCode.CashShortage));
            Assert.True(firstSubmission.IsAccepted);

            var duplicateSourceSubmission = await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                ActivityCode: "RFFO",
                BranchCode: "36:01",
                IncomingStatus: BranchActivityStatus.Completed,
                TelegramMessageLogId: 2003,
                ExplicitReportingDate: Day1,
                PendingReason: PendingReasonCode.CashShortage));

            Assert.False(duplicateSourceSubmission.IsAccepted);
            Assert.Equal(MessageProcessingStatus.DuplicateIgnored, duplicateSourceSubmission.ProcessingStatus);

            // Branch 36:01 status must still be Pending (second message with duplicate ID 2003 was ignored)
            var currentStatus = await context.ActivityStatuses
                .AsNoTracking()
                .SingleAsync(s => s.ReportingDate == Day1);
            Assert.Equal(BranchActivityStatus.Pending, currentStatus.CurrentStatus);

            // 4. Disable RFFO activity -> subsequent updates rejected as RejectedUnknownActivity
            await configService.UpsertActivityAsync(new UpsertActivityMasterRequest(
                ActivityCode: "RFFO",
                ActivityName: "Request for Field Force Operations",
                IsActive: false,
                ChangedBy: "super.admin",
                ChangeReason: "Temporarily disable RFFO"));

            var disabledActivityResult = await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                ActivityCode: "RFFO",
                BranchCode: "36:01",
                IncomingStatus: BranchActivityStatus.Completed,
                TelegramMessageLogId: 2004,
                ExplicitReportingDate: Day1,
                PendingReason: PendingReasonCode.CashShortage));

            Assert.False(disabledActivityResult.IsAccepted);
            Assert.Equal(MessageProcessingStatus.RejectedUnknownActivity, disabledActivityResult.ProcessingStatus);

            await tx.RollbackAsync();
        });
    }

    // =========================================================================
    // 3. Reopening Permissions: Grant, Revoke, Consume, Expiry, Cross-Branch & Reactivation
    // =========================================================================

    [Fact]
    public async Task ReopenPermissions_GrantRevokeConsumeExpiryCrossBranchAndCycleReactivation_ShouldEnforceAllSafeguards()
    {
        await using var context = CreateContext();
        if (!await context.Database.CanConnectAsync())
        {
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync();
            var (orgService, _, processor, _, reopenService, _) = CreateServices(context);

            await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: new DateOnly(2026, 11, 1));

            // Complete both 36:01 and 36:02 on Day 2 and create a Completed ReportingCycle
            await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                "RFFO", "36:01", BranchActivityStatus.Completed, TelegramMessageLogId: 3001, ExplicitReportingDate: Day2));
            await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                "RFFO", "36:02", BranchActivityStatus.Completed, TelegramMessageLogId: 3002, ExplicitReportingDate: Day2));

            var cycle = new ReportingCycle
            {
                ActivityId = 1,
                ReportingDate = Day2,
                CycleStatus = ReportingCycleStatus.Active
            };
            cycle.Slots.Add(new ReportingSlot
            {
                ActivityId = 1,
                ReportingDate = Day2,
                ScheduledSlotTime = new TimeOnly(15, 0),
                ScheduledExecutionUtc = new DateTime(2026, 11, 8, 9, 30, 0, DateTimeKind.Utc),
                SlotStatus = ReportDeliveryStatus.Pending
            });
            cycle.TryCompleteWhenAllBranchesCompleted(2, 2, new DateTime(2026, 11, 8, 6, 0, 0, DateTimeKind.Utc));
            context.ReportingCycles.Add(cycle);
            await context.SaveChangesAsync();

            // 1. Grant a permission on Day 1 (yesterday) for 36:01 -> must automatically expire when Day 2 update is processed
            var staleGrant = await reopenService.GrantPermissionAsync(new GrantStatusReopenPermissionRequest(
                ActivityCode: "RFFO",
                BranchCode: "36:01",
                ReportingDate: Day1,
                EnabledBy: "admin.ops",
                Reason: "Stale Day 1 permission"));
            Assert.True(staleGrant.IsSuccess);

            // Attempt Completed -> Pending on Day 2 with only Day 1 permission present -> rejected & Day 1 permission expired!
            var attemptWithOnlyStalePermission = await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                "RFFO", "36:01", BranchActivityStatus.Pending, TelegramMessageLogId: 3003, ExplicitReportingDate: Day2, PendingReason: PendingReasonCode.CashShortage));
            Assert.False(attemptWithOnlyStalePermission.IsAccepted);

            var expiredReloaded = await context.StatusReopenPermissions
                .AsNoTracking()
                .SingleAsync(p => p.PermissionId == staleGrant.Permission!.PermissionId);
            Assert.Equal(ReopenPermissionStatus.Expired, expiredReloaded.PermissionStatus);

            // 2. Grant a permission on Day 2 for 36:02 -> must NOT allow 36:01 to reopen
            var grantFor3602 = await reopenService.GrantPermissionAsync(new GrantStatusReopenPermissionRequest(
                ActivityCode: "RFFO",
                BranchCode: "36:02",
                ReportingDate: Day2,
                EnabledBy: "admin.ops",
                Reason: "Permission for 36:02 only"));
            Assert.True(grantFor3602.IsSuccess);

            var wrongBranchAttempt = await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                "RFFO", "36:01", BranchActivityStatus.Pending, TelegramMessageLogId: 3004, ExplicitReportingDate: Day2, PendingReason: PendingReasonCode.CashShortage));
            Assert.False(wrongBranchAttempt.IsAccepted);

            // 3. Grant a permission on Day 2 for 36:01, then revoke it -> must NOT allow 36:01 to reopen
            var grantToRevoke = await reopenService.GrantPermissionAsync(new GrantStatusReopenPermissionRequest(
                ActivityCode: "RFFO",
                BranchCode: "36:01",
                ReportingDate: Day2,
                EnabledBy: "admin.ops",
                Reason: "Grant to be revoked"));
            Assert.True(grantToRevoke.IsSuccess);

            // Duplicate Available permission for same Branch + Activity + Date must be rejected
            var duplicateGrant = await reopenService.GrantPermissionAsync(new GrantStatusReopenPermissionRequest(
                ActivityCode: "RFFO",
                BranchCode: "36:01",
                ReportingDate: Day2,
                EnabledBy: "admin.ops",
                Reason: "Duplicate available grant"));
            Assert.False(duplicateGrant.IsSuccess);
            Assert.Equal("DuplicateAvailablePermission", duplicateGrant.ErrorCode);

            var revokeResult = await reopenService.RevokePermissionAsync(new RevokeStatusReopenPermissionRequest(
                PermissionId: grantToRevoke.Permission!.PermissionId,
                RevokedBy: "super.admin",
                Reason: "Revoked before consumption"));
            Assert.True(revokeResult.IsSuccess);
            Assert.Equal(ReopenPermissionStatus.Revoked, revokeResult.Permission!.PermissionStatus);

            var attemptAfterRevoke = await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                "RFFO", "36:01", BranchActivityStatus.Pending, TelegramMessageLogId: 3005, ExplicitReportingDate: Day2, PendingReason: PendingReasonCode.CashShortage));
            Assert.False(attemptAfterRevoke.IsAccepted);

            // 4. Grant a fresh permission on Day 2 for 36:01 and consume it via Pending update
            var validGrant = await reopenService.GrantPermissionAsync(new GrantStatusReopenPermissionRequest(
                ActivityCode: "RFFO",
                BranchCode: "36:01",
                ReportingDate: Day2,
                EnabledBy: "admin.ops",
                Reason: "Authorised recount for 36:01"));
            Assert.True(validGrant.IsSuccess);

            var reopenSuccess = await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                ActivityCode: "RFFO",
                BranchCode: "36:01",
                IncomingStatus: BranchActivityStatus.Pending,
                TelegramMessageLogId: 3006,
                TelegramUserId: 88001,
                SenderDisplayName: "Branch 36:01 User",
                OccurredAtUtc: new DateTime(2026, 11, 8, 7, 0, 0, DateTimeKind.Utc),
                ExplicitReportingDate: Day2,
                PendingReason: PendingReasonCode.CashShortage));

            Assert.True(reopenSuccess.IsAccepted);
            Assert.Equal(TransitionOutcome.AcceptedViaReopen, reopenSuccess.Outcome);
            Assert.True(reopenSuccess.DidConsumeReopenPermission);
            Assert.Equal(validGrant.Permission!.PermissionId, reopenSuccess.ConsumedPermissionId);
            Assert.True(reopenSuccess.DidReactivateReportingCycle);

            // ReportingCycle must be Active again and future slot 15:00 IST restored to Pending
            var reactivatedCycle = await context.ReportingCycles
                .Include(c => c.Slots)
                .AsNoTracking()
                .SingleAsync(c => c.ReportingCycleId == cycle.ReportingCycleId);
            Assert.Equal(ReportingCycleStatus.Active, reactivatedCycle.CycleStatus);
            Assert.Equal(ReportDeliveryStatus.Pending, reactivatedCycle.Slots.Single().SlotStatus);

            // Attempt to revoke the already-consumed permission must fail
            var revokeConsumed = await reopenService.RevokePermissionAsync(new RevokeStatusReopenPermissionRequest(
                PermissionId: validGrant.Permission.PermissionId,
                RevokedBy: "super.admin",
                Reason: "Cannot revoke consumed"));
            Assert.False(revokeConsumed.IsSuccess);
            Assert.Equal("InvalidPermissionState", revokeConsumed.ErrorCode);

            // Complete 36:01 again, then attempt a second Completed -> Pending without a new permission -> rejected
            await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                "RFFO", "36:01", BranchActivityStatus.Completed, TelegramMessageLogId: 3007, ExplicitReportingDate: Day2));

            var secondReopenWithoutNewGrant = await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                "RFFO", "36:01", BranchActivityStatus.Pending, TelegramMessageLogId: 3008, ExplicitReportingDate: Day2, PendingReason: PendingReasonCode.CashShortage));
            Assert.False(secondReopenWithoutNewGrant.IsAccepted);

            // Reopening history must preserve all 4 permissions (Expired, Available on 36:02, Revoked, Consumed)
            var historyList = await reopenService.GetPermissionsHistoryAsync(
                new StatusReopenPermissionFilterQuery(ActivityCode: "RFFO"));
            Assert.Equal(4, historyList.Count);

            await tx.RollbackAsync();
        });
    }

    // =========================================================================
    // 4. Atomic Rollback When History Persistence Fails & Concurrency Protection
    // =========================================================================

    [Fact]
    public async Task RffoProcessing_WhenHistoryPersistenceFails_ShouldRollbackStatusAndPermissionChangesAtomically()
    {
        await using var context = CreateContext();
        if (!await context.Database.CanConnectAsync())
        {
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync();
            var (orgService, _, processor, _, _, _) = CreateServices(context);

            await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: new DateOnly(2026, 11, 1));

            // First valid submission sets 36:01 to Pending with TelegramMessageLogId = 4001
            var first = await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                "RFFO", "36:01", BranchActivityStatus.Pending, TelegramMessageLogId: 4001, ExplicitReportingDate: Day1, PendingReason: PendingReasonCode.CashShortage));
            Assert.True(first.IsAccepted);

            // Force a database failure during the ActivityHistory INSERT of the next transition by passing a
            // SenderUsername that exceeds the SQL column max length (nvarchar(150)) with string truncation error.
            var oversizedUsername = new string('X', 400);

            await Assert.ThrowsAsync<DbUpdateException>(() =>
                processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                    ActivityCode: "RFFO",
                    BranchCode: "36:01",
                    IncomingStatus: BranchActivityStatus.Completed,
                    TelegramMessageLogId: 4002,
                    SenderUsername: oversizedUsername,
                    ExplicitReportingDate: Day1)));

            // Verify that the savepoint rollback left 36:01's ActivityStatus at Pending (not Completed)
            // and only 1 ActivityHistory row exists!
            var statusInDb = await context.ActivityStatuses
                .AsNoTracking()
                .SingleAsync(s => s.ReportingDate == Day1);
            Assert.Equal(BranchActivityStatus.Pending, statusInDb.CurrentStatus);

            var historyCount = await context.ActivityHistories
                .AsNoTracking()
                .CountAsync(h => h.ReportingDate == Day1);
            Assert.Equal(1, historyCount);

            await tx.RollbackAsync();
        });
    }

    [Fact]
    public async Task ConcurrencySafeguards_SimultaneousStatusUpdateAndPermissionConsumption_ShouldThrowDbUpdateConcurrencyException()
    {
        await using var context = CreateContext();
        if (!await context.Database.CanConnectAsync())
        {
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync();
            var (orgService, _, processor, _, reopenService, _) = CreateServices(context);

            await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: new DateOnly(2026, 11, 1));

            await processor.ProcessParsedUpdateAsync(new ActivityUpdateSubmissionCommand(
                "RFFO", "36:01", BranchActivityStatus.Completed, TelegramMessageLogId: 5001, ExplicitReportingDate: Day1));

            var grant = await reopenService.GrantPermissionAsync(new GrantStatusReopenPermissionRequest(
                "RFFO", "36:01", Day1, "admin.ops", "Concurrent consume test"));
            Assert.True(grant.IsSuccess);

            var permission = await context.StatusReopenPermissions
                .SingleAsync(p => p.PermissionId == grant.Permission!.PermissionId);
            var stalePermissionRowVersion = permission.RowVersion.ToArray();

            // First caller consumes the permission
            permission.TryConsume(permission.BranchId, permission.ActivityId, Day1, 9001, 5002, DateTime.UtcNow, out _);
            await context.SaveChangesAsync();

            // Second concurrent caller attempts to update/consume using the stale RowVersion token
            permission.PermissionStatus = ReopenPermissionStatus.Consumed;
            permission.ConsumedMessageId = 5003;
            context.Entry(permission).Property(p => p.RowVersion).OriginalValue = stalePermissionRowVersion;

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync());

            await tx.RollbackAsync();
        });
    }

    // =========================================================================
    // 5. Activity Configuration Service: Create/Update, Enable/Disable, Key-Value, Effective Schedules & Audit History
    // =========================================================================

    [Fact]
    public async Task ActivityConfigurationService_ShouldManageActivitiesKeyValuesEffectiveSchedulesAndPreserveAuditHistory()
    {
        await using var context = CreateContext();
        if (!await context.Database.CanConnectAsync())
        {
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync();
            var (_, _, _, _, _, configService) = CreateServices(context);

            // 1. Create a new activity GETDATA
            var createResult = await configService.UpsertActivityAsync(new UpsertActivityMasterRequest(
                ActivityCode: " getdata ",
                ActivityName: " GetData Operational Sync ",
                Description: " Daily branch data sync monitoring ",
                IsActive: true,
                ChangedBy: "super.admin",
                ChangeReason: "Onboard GetData activity"));

            Assert.True(createResult.IsSuccess);
            Assert.NotNull(createResult.Activity);
            Assert.Equal("GETDATA", createResult.Activity!.ActivityCode);
            Assert.True(createResult.Activity.IsActive);

            // 2. Disable and re-enable GETDATA
            var disableResult = await configService.UpsertActivityAsync(new UpsertActivityMasterRequest(
                ActivityCode: "GETDATA",
                ActivityName: "GetData Operational Sync",
                Description: "Daily branch data sync monitoring",
                IsActive: false,
                ChangedBy: "super.admin",
                ChangeReason: "Pause GetData during maintenance"));
            Assert.True(disableResult.IsSuccess);
            Assert.False(disableResult.Activity!.IsActive);

            var activeOnlyList = await configService.GetActivitiesAsync(includeInactive: false);
            Assert.DoesNotContain(activeOnlyList, a => a.ActivityCode == "GETDATA");

            var enableResult = await configService.UpsertActivityAsync(new UpsertActivityMasterRequest(
                ActivityCode: "GETDATA",
                ActivityName: "GetData Operational Sync v2",
                Description: "Re-enabled GetData",
                IsActive: true,
                ChangedBy: "super.admin",
                ChangeReason: "Resume GetData"));
            Assert.True(enableResult.IsSuccess);
            Assert.True(enableResult.Activity!.IsActive);

            // 3. Set and update activity key-value configuration
            var kvCreate = await configService.SetActivityConfigurationValueAsync(new SetActivityKeyValueConfigRequest(
                ActivityCode: "GETDATA",
                ConfigKey: " batch_timeout_sec ",
                ConfigValue: " 120 ",
                Description: "Timeout for GetData batch",
                ChangedBy: "admin.ops",
                ChangeReason: "Initial timeout"));
            Assert.True(kvCreate.IsSuccess);
            Assert.Single(kvCreate.Activity!.Settings);

            var kvUpdate = await configService.SetActivityConfigurationValueAsync(new SetActivityKeyValueConfigRequest(
                ActivityCode: "GETDATA",
                ConfigKey: "BATCH_TIMEOUT_SEC",
                ConfigValue: "180",
                Description: "Increased timeout",
                ChangedBy: "admin.ops",
                ChangeReason: "Increase timeout to 180s"));
            Assert.True(kvUpdate.IsSuccess);
            Assert.Equal("180", Assert.Single(kvUpdate.Activity!.Settings).ConfigValue);

            // 4. Configure initial effective-dated reporting schedule for GETDATA (2026-01-01 onwards: 10:00 to 16:00 every 60 mins)
            var schedule1 = await configService.UpdateReportingConfigurationAsync(new UpdateActivityReportingConfigurationRequest(
                ActivityCode: "GETDATA",
                ReportingStartTime: new TimeOnly(10, 0),
                ReportingEndTime: new TimeOnly(16, 0),
                ReportingIntervalMinutes: 60,
                TimeZoneId: "Asia/Kolkata",
                IsReportingEnabled: true,
                StopWhenAllCompleted: true,
                EffectiveFrom: new DateOnly(2026, 1, 1),
                ChangedBy: "admin.ops",
                ChangeReason: "Initial GetData schedule"));

            Assert.True(schedule1.IsSuccess);
            Assert.NotNull(schedule1.ActiveConfiguration);
            Assert.Equal(7, schedule1.ActiveConfiguration!.CalculatedDailySlotTimes.Count);

            // 5. Rollover schedule effective 2026-06-01 (09:00 to 18:00 every 30 mins -> auto-closes previous period on 2026-05-31)
            var schedule2 = await configService.UpdateReportingConfigurationAsync(new UpdateActivityReportingConfigurationRequest(
                ActivityCode: "GETDATA",
                ReportingStartTime: new TimeOnly(9, 0),
                ReportingEndTime: new TimeOnly(18, 0),
                ReportingIntervalMinutes: 30,
                TimeZoneId: "Asia/Kolkata",
                IsReportingEnabled: true,
                StopWhenAllCompleted: true,
                EffectiveFrom: new DateOnly(2026, 6, 1),
                ChangedBy: "admin.ops",
                ChangeReason: "Expand GetData reporting window"));

            Assert.True(schedule2.IsSuccess);
            Assert.Equal(19, schedule2.ActiveConfiguration!.CalculatedDailySlotTimes.Count);

            // Query effective schedule as of historical date 2026-03-15 -> must return 60-min schedule (7 slots)
            var asOfMarch = await configService.GetReportingConfigurationAsync("GETDATA", new DateOnly(2026, 3, 15));
            Assert.NotNull(asOfMarch?.ResolvedConfigurationAsOfDate);
            Assert.Equal(60, asOfMarch!.ResolvedConfigurationAsOfDate!.ReportingIntervalMinutes);
            Assert.Equal(new DateOnly(2026, 5, 31), asOfMarch.ResolvedConfigurationAsOfDate.EffectiveTo);

            // Query effective schedule as of 2026-07-15 -> must return 30-min schedule (19 slots)
            var asOfJuly = await configService.GetReportingConfigurationAsync("GETDATA", new DateOnly(2026, 7, 15));
            Assert.NotNull(asOfJuly?.ResolvedConfigurationAsOfDate);
            Assert.Equal(30, asOfJuly!.ResolvedConfigurationAsOfDate!.ReportingIntervalMinutes);
            Assert.Null(asOfJuly.ResolvedConfigurationAsOfDate.EffectiveTo);

            // 6. Overlapping schedule rejection: attempting to insert a schedule starting 2026-04-01 (overlapping closed period 2026-01-01..2026-05-31)
            var overlappingResult = await configService.UpdateReportingConfigurationAsync(new UpdateActivityReportingConfigurationRequest(
                ActivityCode: "GETDATA",
                ReportingStartTime: new TimeOnly(8, 0),
                ReportingEndTime: new TimeOnly(17, 0),
                ReportingIntervalMinutes: 60,
                TimeZoneId: "Asia/Kolkata",
                IsReportingEnabled: true,
                StopWhenAllCompleted: true,
                EffectiveFrom: new DateOnly(2026, 4, 1),
                ChangedBy: "admin.ops"));

            Assert.False(overlappingResult.IsSuccess);
            Assert.Equal("OverlappingEffectivePeriod", overlappingResult.ErrorCode);

            // 7. Invalid schedule rejection: end time before start time throws ArgumentException
            await Assert.ThrowsAsync<ArgumentException>(() =>
                configService.UpdateReportingConfigurationAsync(new UpdateActivityReportingConfigurationRequest(
                    ActivityCode: "GETDATA",
                    ReportingStartTime: new TimeOnly(18, 0),
                    ReportingEndTime: new TimeOnly(9, 0),
                    ReportingIntervalMinutes: 30,
                    TimeZoneId: "Asia/Kolkata",
                    IsReportingEnabled: true,
                    StopWhenAllCompleted: true,
                    EffectiveFrom: new DateOnly(2026, 12, 1),
                    ChangedBy: "admin.ops")));

            // 8. Verify ConfigurationHistory audit preservation across ActivityMaster, ActivityConfiguration, and ActivityReportingConfiguration
            var allAuditEntries = await configService.GetConfigurationHistoryAsync();
            Assert.Contains(allAuditEntries, e => e.EntityName == nameof(ActivityMaster) && e.EntityKey == "GETDATA" && e.ActionType == "Create");
            Assert.Contains(allAuditEntries, e => e.EntityName == nameof(ActivityMaster) && e.EntityKey == "GETDATA" && e.ActionType == "Disable");
            Assert.Contains(allAuditEntries, e => e.EntityName == nameof(ActivityMaster) && e.EntityKey == "GETDATA" && e.ActionType == "Enable");
            Assert.Contains(allAuditEntries, e => e.EntityName == nameof(ActivityConfiguration) && e.EntityKey == "GETDATA:BATCH_TIMEOUT_SEC" && e.ActionType == "Update");
            Assert.Contains(allAuditEntries, e => e.EntityName == nameof(ActivityReportingConfiguration) && e.EntityKey == "GETDATA" && e.ActionType == "RolloverEffectivePeriod");

            await tx.RollbackAsync();
        });
    }
}
