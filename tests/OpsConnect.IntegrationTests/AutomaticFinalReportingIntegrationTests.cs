using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpsConnect.Api.Controllers;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Activities.RFFO;
using OpsConnect.Application.Common;
using OpsConnect.Application.Organisation;
using OpsConnect.Application.Reporting;
using OpsConnect.Application.Telegram;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Organisation;
using OpsConnect.Domain.Reporting;
using OpsConnect.Domain.Telegram;
using OpsConnect.Infrastructure.Persistence;
using OpsConnect.Infrastructure.Services;
using OpsConnect.Infrastructure.Telegram;
using Xunit;

namespace OpsConnect.IntegrationTests;

[Collection("SqlServerDatabase")]
public class AutomaticFinalReportingIntegrationTests
{
    private static readonly DateTime TestUtcNow = new(2026, 11, 10, 3, 30, 0, DateTimeKind.Utc); // 09:00 IST
    private static readonly DateOnly TestDate = new(2026, 11, 10);

    private static string Msg(string branch, string status) =>
        $"Activity : RFFO\nBranch Code : {branch}\nStatus : {status}";

    private static OpsConnectDbContext CreateContext()
    {
        var factory = new OpsConnectDbContextFactory();
        return factory.CreateDbContext(Array.Empty<string>());
    }

    private static (
        IOrganisationMasterService OrgService,
        IActivityReportingService ReportingService,
        IScheduledReportingService ScheduledService,
        ActivityUpdateProcessor Processor,
        RecordingTestBotClient BotClient,
        ReportingController Controller)
        CreateTestHarness(OpsConnectDbContext dbContext, DateTime? fixedUtcNow = null, DateOnly? fixedDate = null)
    {
        var utcNow = fixedUtcNow ?? TestUtcNow;
        var date = fixedDate ?? TestDate;
        var clock = new FixedTestClock(utcNow, date);

        var orgService = new OrganisationMasterService(
            dbContext,
            new OrganisationExcelReader(),
            new OrganisationHierarchyImportValidator(),
            clock);
        var reportingService = new ActivityReportingService(dbContext, clock);
        var formatter = new TelegramReportFormatter();
        var botClient = new RecordingTestBotClient();

        var botOptions = new TelegramBotOptions
        {
            BotToken = "123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11",
            WebhookSecretToken = "test-webhook-secret-token",
            IsEnabled = true
        };
        var botOptionsMonitor = new StaticOptionsMonitor<TelegramBotOptions>(botOptions);

        var scheduledService = new ScheduledReportingService(
            dbContext,
            reportingService,
            formatter,
            botClient,
            clock,
            botOptionsMonitor,
            NullLogger<ScheduledReportingService>.Instance);

        var rffoHandler = new RffoActivityUpdateHandler(
            dbContext,
            clock,
            scheduledService,
            reportingService);

        var parser = new TelegramActivityMessageParser();
        var processor = new ActivityUpdateProcessor(parser, new[] { rffoHandler });

        var principal = CreatePrincipal("admin.test", SystemRoles.Admin);
        var controller = new ReportingController(reportingService, scheduledService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };

        return (orgService, reportingService, scheduledService, processor, botClient, controller);
    }

    private static async Task SeedHierarchyAndActivityAsync(
        IOrganisationMasterService orgService,
        OpsConnectDbContext dbContext,
        DateOnly effectiveDate)
    {
        var rows = new[]
        {
            new OrganisationImportRow(
                RowNumber: 2, ZoneCode: "EAST", ZoneName: "East Zone",
                StateCode: "OD", StateName: "Odisha",
                RegionCode: "REG-A", RegionName: "Region A",
                BranchCode: "36:01", BranchName: "Bhubaneswar Main",
                IsZoneActive: true, IsStateActive: true, IsRegionActive: true, IsBranchActive: true),
            new OrganisationImportRow(
                RowNumber: 3, ZoneCode: "EAST", ZoneName: "East Zone",
                StateCode: "OD", StateName: "Odisha",
                RegionCode: "REG-A", RegionName: "Region A",
                BranchCode: "36:02", BranchName: "Cuttack City",
                IsZoneActive: true, IsStateActive: true, IsRegionActive: true, IsBranchActive: true)
        };

        await orgService.ImportHierarchyAsync(rows, effectiveFromDate: effectiveDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == ActivityModuleConstants.RffoActivityCode);

        var existingConfig = await dbContext.ActivityReportingConfigurations
            .FirstOrDefaultAsync(c => c.ActivityId == activity.ActivityId);

        if (existingConfig is null)
        {
            var config = ActivityReportingConfiguration.Create(
                activityId: activity.ActivityId,
                reportingStartTime: new TimeOnly(9, 0),
                reportingEndTime: new TimeOnly(18, 0),
                reportingIntervalMinutes: 30,
                timeZoneId: "Asia/Kolkata",
                effectiveFrom: effectiveDate,
                isReportingEnabled: true,
                stopWhenAllCompleted: true);

            dbContext.ActivityReportingConfigurations.Add(config);
            await dbContext.SaveChangesAsync();
        }
    }

    private static async Task ClearExistingDestinationGroupsAsync(OpsConnectDbContext dbContext, int activityId)
    {
        var existing = await dbContext.TelegramGroups
            .Where(g => g.ActivityId == activityId && g.GroupRole == TelegramGroupRole.Destination && g.IsActive)
            .ToListAsync();

        foreach (var g in existing)
        {
            g.IsActive = false;
        }

        if (existing.Count > 0)
        {
            await dbContext.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task LastBranchCompletion_ShouldTriggerImmediateFinalReport_AndCloseReportingCycle()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, processor, botClient, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        // Add active destination group
        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100999001,
            GroupTitle = "RFFO Final Broadcast",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        // Ensure daily cycle and slots exist
        await scheduledService.DispatchDueReportsAsync(TestUtcNow);
        var initialSentCount = botClient.SentMessages.Count;

        // 1. Branch 36:01 completes -> 1 of 2 branches complete
        var update1 = await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:01", "Completed"),
            TelegramMessageLogId: 101,
            TelegramChatId: -100111,
            TelegramUserId: 501,
            SenderUsername: "user1",
            ExplicitReportingDate: TestDate));

        Assert.True(update1.IsAccepted, $"update1 rejected: {update1.RejectionReason} - reply: {update1.ReplyMessage}");
        Assert.False(update1.DidTriggerImmediateFinalReportCondition);

        // Cycle should be active, zero final report messages sent
        var cycleBefore = await scheduledService.GetReportingCycleStatusAsync("RFFO", TestDate);
        Assert.NotNull(cycleBefore);
        Assert.Equal(ReportingCycleStatus.Active, cycleBefore.CycleStatus);
        Assert.Equal(initialSentCount, botClient.SentMessages.Count);

        // 2. Branch 36:02 completes -> 2 of 2 branches complete (100% completion!)
        var update2 = await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:02", "Completed"),
            TelegramMessageLogId: 102,
            TelegramChatId: -100111,
            TelegramUserId: 502,
            SenderUsername: "user2",
            ExplicitReportingDate: TestDate));

        Assert.True(update2.IsAccepted);
        Assert.True(update2.DidTriggerImmediateFinalReportCondition);

        // Verify Telegram message was immediately delivered
        Assert.Equal(initialSentCount + 1, botClient.SentMessages.Count);
        var finalMsg = botClient.SentMessages.Last();
        Assert.Equal(-100999001, finalMsg.ChatId);
        Assert.Contains("Request for Field Force Operations — Daily Status", finalMsg.Text);

        // Verify ReportingCycle is now Completed and closed
        var cycleAfter = await scheduledService.GetReportingCycleStatusAsync("RFFO", TestDate);
        Assert.NotNull(cycleAfter);
        Assert.Equal(ReportingCycleStatus.Completed, cycleAfter.CycleStatus);
        Assert.Equal(ReportingCycleStopReason.AllBranchesCompleted, cycleAfter.StopReason);
        Assert.Equal(1, cycleAfter.CompletionSequenceNumber);
        Assert.NotNull(cycleAfter.CompletedAtUtc);

        // Verify immutable SummaryLog was persisted with ImmediateFinalAllCompleted trigger type
        var finalKey = SummaryLog.BuildImmediateFinalIdempotencyKey(activity.ActivityId, TestDate, 1);
        var summary = await dbContext.SummaryLogs.Include(s => s.Deliveries).SingleAsync(s => s.IdempotencyKey == finalKey);
        Assert.Equal(ReportingTriggerType.ImmediateFinalAllCompleted, summary.TriggerType);
        Assert.Null(summary.ScheduledSlotTime);
        Assert.Equal(2, summary.TotalActiveBranches);
        Assert.Equal(2, summary.CompletedBranches);
        Assert.Equal(0, summary.PendingBranches);
        Assert.Equal(0, summary.NotReportedBranches);
        Assert.Equal(100.00m, summary.CompletionPercentage);
        Assert.Single(summary.Deliveries);
        Assert.Equal(ReportDeliveryStatus.Delivered, summary.Deliveries.First().DeliveryStatus);

        // Verify historical delivered slot remains Delivered and subsequent slots are marked SkippedAllCompleted
        var slots = await scheduledService.GetScheduledSlotsAsync("RFFO", TestDate);
        Assert.NotEmpty(slots);
        Assert.Equal(ReportDeliveryStatus.Delivered, slots[0].SlotStatus);
        Assert.All(slots.Skip(1), s => Assert.Equal(ReportDeliveryStatus.SkippedAllCompleted, s.SlotStatus));

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task FinalReport_ShouldNotBeTriggered_BeforeAllBranchesComplete()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, processor, botClient, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100999002,
            GroupTitle = "RFFO Final Broadcast",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        // Ensure daily slots and cycle exist
        await scheduledService.DispatchDueReportsAsync(TestUtcNow);
        var initialSentCount = botClient.SentMessages.Count;

        // Branch 36:01 -> Pending
        var p1 = await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:01", "Pending"),
            TelegramMessageLogId: 201,
            TelegramChatId: -100111,
            TelegramUserId: 501,
            SenderUsername: "user1",
            ExplicitReportingDate: TestDate));

        Assert.True(p1.IsAccepted);
        Assert.False(p1.DidTriggerImmediateFinalReportCondition);

        // Branch 36:02 -> Completed (Branch 36:01 is still Pending!)
        var c2 = await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:02", "Completed"),
            TelegramMessageLogId: 202,
            TelegramChatId: -100111,
            TelegramUserId: 502,
            SenderUsername: "user2",
            ExplicitReportingDate: TestDate));

        Assert.True(c2.IsAccepted);
        Assert.False(c2.DidTriggerImmediateFinalReportCondition);

        // No new final report sent
        Assert.Equal(initialSentCount, botClient.SentMessages.Count);

        // Cycle remains Active
        var cycle = await scheduledService.GetReportingCycleStatusAsync("RFFO", TestDate);
        Assert.NotNull(cycle);
        Assert.Equal(ReportingCycleStatus.Active, cycle.CycleStatus);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task DuplicateUpdate_ShouldNotCreateDuplicateFinalReport()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, processor, botClient, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100999003,
            GroupTitle = "RFFO Final Broadcast",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        // Complete both branches
        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:01", "Completed"),
            TelegramMessageLogId: 301,
            TelegramChatId: -100111,
            TelegramUserId: 501,
            SenderUsername: "user1",
            ExplicitReportingDate: TestDate));

        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:02", "Completed"),
            TelegramMessageLogId: 302,
            TelegramChatId: -100111,
            TelegramUserId: 502,
            SenderUsername: "user2",
            ExplicitReportingDate: TestDate));

        Assert.Single(botClient.SentMessages);

        // Submit duplicate Completed for 36:02 (AcceptedUnchanged)
        var dupResult = await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:02", "Completed"),
            TelegramMessageLogId: 303,
            TelegramChatId: -100111,
            TelegramUserId: 502,
            SenderUsername: "user2",
            ExplicitReportingDate: TestDate));

        Assert.True(dupResult.IsAccepted);
        Assert.False(dupResult.DidStatusChange);
        Assert.False(dupResult.DidTriggerImmediateFinalReportCondition);

        // No new Telegram message sent
        Assert.Single(botClient.SentMessages);

        // Exactly one SummaryLog exists for final report
        var finalSummaries = await dbContext.SummaryLogs
            .Where(s => s.ActivityId == activity.ActivityId &&
                        s.ReportingDate == TestDate &&
                        s.TriggerType == ReportingTriggerType.ImmediateFinalAllCompleted)
            .ToListAsync();

        Assert.Single(finalSummaries);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task FinalReportDeliveryFailure_ShouldKeepCycleActive_AndCloseOnlyAfterSuccessfulRetry()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, processor, botClient, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100999004,
            GroupTitle = "RFFO Final Broadcast",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        // Complete first branch normally
        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:01", "Completed"),
            TelegramMessageLogId: 401,
            TelegramChatId: -100111,
            TelegramUserId: 501,
            SenderUsername: "user1",
            ExplicitReportingDate: TestDate));

        // Script bot client to simulate transient Telegram 500 failure on final report send
        botClient.SetSendHandler((chatId, text, replyId) =>
            Task.FromResult(new TelegramBotSendResult(false, null, 500, "Simulated Telegram 500 transient failure")));

        // Complete second branch: triggers final report which FAILS delivery
        var finalUpdate = await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:02", "Completed"),
            TelegramMessageLogId: 402,
            TelegramChatId: -100111,
            TelegramUserId: 502,
            SenderUsername: "user2",
            ExplicitReportingDate: TestDate));

        Assert.True(finalUpdate.IsAccepted);
        Assert.True(finalUpdate.DidTriggerImmediateFinalReportCondition);

        // Verification: Cycle MUST NOT be marked Completed when delivery fails!
        var cycleAfterFailure = await scheduledService.GetReportingCycleStatusAsync("RFFO", TestDate);
        Assert.NotNull(cycleAfterFailure);
        Assert.Equal(ReportingCycleStatus.Active, cycleAfterFailure.CycleStatus);
        Assert.Null(cycleAfterFailure.CompletedAtUtc);

        // Verify SummaryLog snapshot WAS created and is immutable
        var finalKey = SummaryLog.BuildImmediateFinalIdempotencyKey(activity.ActivityId, TestDate, 1);
        var summary = await dbContext.SummaryLogs.Include(s => s.Deliveries).SingleAsync(s => s.IdempotencyKey == finalKey);
        Assert.Single(summary.Deliveries);
        var delivery = summary.Deliveries.First();
        Assert.Equal(ReportDeliveryStatus.Failed, delivery.DeliveryStatus);
        Assert.Equal(1, delivery.DeliveryAttempts);
        Assert.Contains("500", delivery.LastError);

        // Reset bot client to succeed on retry
        botClient.SetSendHandler((chatId, text, replyId) =>
            Task.FromResult(new TelegramBotSendResult(true, 7777, 200, null)));

        // Retry final report execution
        var retryResult = await scheduledService.ExecuteImmediateFinalReportAsync("RFFO", TestDate);
        Assert.True(retryResult.IsTriggered);
        Assert.True(retryResult.IsIdempotentRetry);
        Assert.Equal(ReportDeliveryStatus.Delivered, retryResult.DeliveryStatus);
        Assert.Equal(ReportingCycleStatus.Completed, retryResult.CycleStatus);

        // Now the cycle MUST be Completed
        var cycleAfterRetry = await scheduledService.GetReportingCycleStatusAsync("RFFO", TestDate);
        Assert.NotNull(cycleAfterRetry);
        Assert.Equal(ReportingCycleStatus.Completed, cycleAfterRetry.CycleStatus);
        Assert.NotNull(cycleAfterRetry.CompletedAtUtc);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ClosedCycle_ShouldSuppressSubsequentScheduledReports()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, processor, botClient, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100999005,
            GroupTitle = "RFFO Final Broadcast",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        // Ensure daily slots and complete all branches
        await scheduledService.DispatchDueReportsAsync(TestUtcNow); // slot 09:00 executed
        Assert.Single(botClient.SentMessages); // ordinary scheduled report sent

        // Complete both branches -> triggers final report and closes cycle
        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:01", "Completed"),
            TelegramMessageLogId: 501,
            TelegramChatId: -100111,
            TelegramUserId: 501,
            SenderUsername: "user1",
            ExplicitReportingDate: TestDate));

        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:02", "Completed"),
            TelegramMessageLogId: 502,
            TelegramChatId: -100111,
            TelegramUserId: 502,
            SenderUsername: "user2",
            ExplicitReportingDate: TestDate));

        Assert.Equal(2, botClient.SentMessages.Count); // 1 scheduled + 1 final report

        var cycle = await scheduledService.GetReportingCycleStatusAsync("RFFO", TestDate);
        Assert.Equal(ReportingCycleStatus.Completed, cycle!.CycleStatus);

        // Advance clock to 10:00 (slot 09:30 and 10:00 would normally be due)
        var advancedUtcNow = TestUtcNow.AddHours(1);
        var dispatchResult = await scheduledService.DispatchDueReportsAsync(advancedUtcNow);

        // Dispatcher MUST NOT execute any slots for closed cycle
        Assert.Equal(0, dispatchResult.SlotsClaimedAndProcessed);
        Assert.Equal(2, botClient.SentMessages.Count); // No new messages sent!

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task AdministratorReopening_ShouldReactivateCycle_AndSecondCompletionProducesSeq2()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, processor, botClient, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100999006,
            GroupTitle = "RFFO Broadcast",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        // Ensure schedule exists
        await scheduledService.DispatchDueReportsAsync(TestUtcNow);

        // 1. Complete both branches -> Final Report SEQ 1 and cycle closed
        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:01", "Completed"),
            TelegramMessageLogId: 601,
            TelegramChatId: -100111,
            TelegramUserId: 501,
            SenderUsername: "user1",
            ExplicitReportingDate: TestDate));

        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:02", "Completed"),
            TelegramMessageLogId: 602,
            TelegramChatId: -100111,
            TelegramUserId: 502,
            SenderUsername: "user2",
            ExplicitReportingDate: TestDate));

        var cycleSeq1 = await scheduledService.GetReportingCycleStatusAsync("RFFO", TestDate);
        Assert.Equal(ReportingCycleStatus.Completed, cycleSeq1!.CycleStatus);
        Assert.Equal(1, cycleSeq1.CompletionSequenceNumber);

        // 2. Administrator grants reopen permission for branch 36:01
        var branch3601 = await dbContext.Branches.SingleAsync(b => b.BranchCode == "36:01");
        var reopenPermission = StatusReopenPermission.Grant(
            branchId: branch3601.BranchId,
            activityId: activity.ActivityId,
            reportingDate: TestDate,
            enabledBy: "admin.ops",
            reason: "Field verification required correction",
            enabledAtUtc: TestUtcNow);

        dbContext.StatusReopenPermissions.Add(reopenPermission);
        await dbContext.SaveChangesAsync();

        // 3. Valid Pending update arrives for 36:01 -> consumes permission and REACTIVATES cycle
        var reopenUpdate = await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:01", "Pending"),
            TelegramMessageLogId: 603,
            TelegramChatId: -100111,
            TelegramUserId: 501,
            SenderUsername: "user1",
            ExplicitReportingDate: TestDate));

        Assert.True(reopenUpdate.IsAccepted);
        Assert.True(reopenUpdate.DidConsumeReopenPermission);
        Assert.True(reopenUpdate.DidReactivateReportingCycle);

        // Cycle MUST now be Active!
        var cycleReactivated = await scheduledService.GetReportingCycleStatusAsync("RFFO", TestDate);
        Assert.Equal(ReportingCycleStatus.Active, cycleReactivated!.CycleStatus);
        Assert.Null(cycleReactivated.CompletedAtUtc);
        Assert.NotNull(cycleReactivated.LastReactivatedAtUtc);
        Assert.Equal(branch3601.BranchId, cycleReactivated.LastReactivatedByBranchId);

        // Historical SEQ 1 final report in SummaryLog MUST be completely unchanged!
        var seq1Key = SummaryLog.BuildImmediateFinalIdempotencyKey(activity.ActivityId, TestDate, 1);
        var seq1Summary = await dbContext.SummaryLogs.Include(s => s.Deliveries).SingleAsync(s => s.IdempotencyKey == seq1Key);
        Assert.NotNull(seq1Summary);
        Assert.Equal(2, seq1Summary.CompletedBranches);

        // Future slots should be Pending again
        var slotsAfterReopen = await scheduledService.GetScheduledSlotsAsync("RFFO", TestDate);
        Assert.Contains(slotsAfterReopen, s => s.SlotStatus == ReportDeliveryStatus.Pending);

        // 4. Branch 36:01 later completes again -> Triggers SEQ 2 final report!
        var complete2 = await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:01", "Completed"),
            TelegramMessageLogId: 604,
            TelegramChatId: -100111,
            TelegramUserId: 501,
            SenderUsername: "user1",
            ExplicitReportingDate: TestDate));

        Assert.True(complete2.IsAccepted);
        Assert.True(complete2.DidTriggerImmediateFinalReportCondition);

        // Cycle MUST be Completed with sequence number 2!
        var cycleSeq2 = await scheduledService.GetReportingCycleStatusAsync("RFFO", TestDate);
        Assert.Equal(ReportingCycleStatus.Completed, cycleSeq2!.CycleStatus);
        Assert.Equal(2, cycleSeq2.CompletionSequenceNumber);

        // Distinct SEQ 2 SummaryLog exists alongside SEQ 1!
        var seq2Key = SummaryLog.BuildImmediateFinalIdempotencyKey(activity.ActivityId, TestDate, 2);
        var seq2Summary = await dbContext.SummaryLogs.Include(s => s.Deliveries).SingleAsync(s => s.IdempotencyKey == seq2Key);
        Assert.NotNull(seq2Summary);
        Assert.NotEqual(seq1Summary.ReportId, seq2Summary.ReportId);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task InvalidReopening_ShouldNotReactivateCycle()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, processor, _, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100999007,
            GroupTitle = "RFFO Broadcast",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        // Complete both branches
        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:01", "Completed"),
            TelegramMessageLogId: 701,
            TelegramChatId: -100111,
            TelegramUserId: 501,
            SenderUsername: "user1",
            ExplicitReportingDate: TestDate));

        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:02", "Completed"),
            TelegramMessageLogId: 702,
            TelegramChatId: -100111,
            TelegramUserId: 502,
            SenderUsername: "user2",
            ExplicitReportingDate: TestDate));

        var cycle = await scheduledService.GetReportingCycleStatusAsync("RFFO", TestDate);
        Assert.Equal(ReportingCycleStatus.Completed, cycle!.CycleStatus);

        // Unauthorized attempt: Completed -> Pending without admin permission
        var invalidAttempt = await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:01", "Pending"),
            TelegramMessageLogId: 703,
            TelegramChatId: -100111,
            TelegramUserId: 501,
            SenderUsername: "user1",
            ExplicitReportingDate: TestDate));

        Assert.False(invalidAttempt.IsAccepted);
        Assert.False(invalidAttempt.DidReactivateReportingCycle);

        // Cycle MUST remain Completed
        var cycleStillClosed = await scheduledService.GetReportingCycleStatusAsync("RFFO", TestDate);
        Assert.Equal(ReportingCycleStatus.Completed, cycleStillClosed!.CycleStatus);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task MultipartFinalReport_AndPartialDeliveryRecovery()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, _, botClient, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100999008,
            GroupTitle = "RFFO Broadcast",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });

        // Set both branches to Completed in DB
        var b1 = await dbContext.Branches.SingleAsync(b => b.BranchCode == "36:01");
        var b2 = await dbContext.Branches.SingleAsync(b => b.BranchCode == "36:02");
        dbContext.ActivityStatuses.Add(new ActivityStatus
        {
            ActivityId = activity.ActivityId,
            BranchId = b1.BranchId,
            ReportingDate = TestDate,
            CurrentStatus = BranchActivityStatus.Completed,
            UpdatedAtUtc = TestUtcNow
        });
        dbContext.ActivityStatuses.Add(new ActivityStatus
        {
            ActivityId = activity.ActivityId,
            BranchId = b2.BranchId,
            ReportingDate = TestDate,
            CurrentStatus = BranchActivityStatus.Completed,
            UpdatedAtUtc = TestUtcNow
        });
        await dbContext.SaveChangesAsync();

        // Ensure cycle exists
        await scheduledService.DispatchDueReportsAsync(TestUtcNow);

        // Script bot client: Part 1 succeeds (ID 8881), Part 2 fails with 500 error
        int callCount = 0;
        botClient.SetSendHandler((chatId, text, replyId) =>
        {
            callCount++;
            if (callCount == 1)
            {
                return Task.FromResult(new TelegramBotSendResult(true, 8881, 200, null));
            }
            return Task.FromResult(new TelegramBotSendResult(false, null, 500, "Transient failure on part 2"));
        });

        // Pre-insert a 2-part final report snapshot
        var idempotencyKey = SummaryLog.BuildImmediateFinalIdempotencyKey(activity.ActivityId, TestDate, 1);
        var cycleEntity = await dbContext.ReportingCycles.SingleAsync(c => c.ActivityId == activity.ActivityId && c.ReportingDate == TestDate);

        var summary = new SummaryLog
        {
            ReportingCycleId = cycleEntity.ReportingCycleId,
            ActivityId = activity.ActivityId,
            ReportingDate = TestDate,
            TriggerType = ReportingTriggerType.ImmediateFinalAllCompleted,
            ScheduledSlotTime = null,
            IdempotencyKey = idempotencyKey,
            TotalActiveBranches = 2,
            CompletedBranches = 2,
            PendingBranches = 0,
            NotReportedBranches = 0,
            CompletionPercentage = 100.00m,
            FormattedReportContent = "Part 1 Final Report\n\n---\n\nPart 2 Final Report",
            GeneratedAtUtc = TestUtcNow
        };
        dbContext.SummaryLogs.Add(summary);
        await dbContext.SaveChangesAsync();

        // Execute final report: Part 1 succeeds, Part 2 fails
        var firstExec = await scheduledService.ExecuteImmediateFinalReportAsync("RFFO", TestDate);
        Assert.True(firstExec.IsTriggered);
        Assert.Equal(ReportDeliveryStatus.Failed, firstExec.DeliveryStatus);
        Assert.Equal(ReportingCycleStatus.Active, firstExec.CycleStatus); // remains Active!

        // Confirm Part 1 message ID recorded
        var delivery = await dbContext.ReportDeliveryLogs.SingleAsync(d => d.ReportId == summary.ReportId);
        Assert.Equal("8881", delivery.DeliveredTelegramMessageIds);
        Assert.Equal(ReportDeliveryStatus.Failed, delivery.DeliveryStatus);

        // Reset bot client to succeed on retry
        botClient.SetSendHandler((chatId, text, replyId) =>
            Task.FromResult(new TelegramBotSendResult(true, 8882, 200, null)));

        // Retry final report: resumes at Part 2 without resending Part 1
        var secondExec = await scheduledService.ExecuteImmediateFinalReportAsync("RFFO", TestDate);
        Assert.True(secondExec.IsTriggered);
        Assert.Equal(ReportDeliveryStatus.Delivered, secondExec.DeliveryStatus);
        Assert.Equal(ReportingCycleStatus.Completed, secondExec.CycleStatus);

        // Both IDs confirmed
        var updatedDelivery = await dbContext.ReportDeliveryLogs.SingleAsync(d => d.ReportId == summary.ReportId);
        Assert.Equal("8881,8882", updatedDelivery.DeliveredTelegramMessageIds);
        Assert.Equal(ReportDeliveryStatus.Delivered, updatedDelivery.DeliveryStatus);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Security_DispatchFinalEndpoint_ShouldEnforceRoleAuthorisation()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, _, _, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        // 1. Unauthenticated -> 401
        var anonymousController = new ReportingController(
            new ActivityReportingService(dbContext, new FixedTestClock(TestUtcNow, TestDate)),
            scheduledService);

        var unauthResult = await anonymousController.TriggerFinalDispatch("RFFO");
        Assert.IsType<UnauthorizedResult>(unauthResult.Result);

        // 2. Viewer role -> 403 Forbidden
        var viewerController = new ReportingController(
            new ActivityReportingService(dbContext, new FixedTestClock(TestUtcNow, TestDate)),
            scheduledService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal("viewer.ops", SystemRoles.Viewer) }
            }
        };

        var forbidResult = await viewerController.TriggerFinalDispatch("RFFO");
        Assert.IsType<ForbidResult>(forbidResult.Result);

        // 3. Admin role -> 200 OK
        var adminController = new ReportingController(
            new ActivityReportingService(dbContext, new FixedTestClock(TestUtcNow, TestDate)),
            scheduledService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal("admin.ops", SystemRoles.Admin) }
            }
        };

        var okResult = await adminController.TriggerFinalDispatch("RFFO");
        var okObj = Assert.IsType<OkObjectResult>(okResult.Result);
        var finalData = Assert.IsType<ImmediateFinalReportResult>(okObj.Value);
        Assert.False(finalData.IsTriggered); // Not all completed yet

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task EmptyBranchPopulation_ShouldNotTriggerCompletion()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, _, _, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        // Deactivate all branches to create an empty active population
        var allBranches = await dbContext.Branches.ToListAsync();
        foreach (var b in allBranches)
        {
            b.IsActive = false;
        }
        await dbContext.SaveChangesAsync();

        var result = await scheduledService.ExecuteImmediateFinalReportAsync("RFFO", TestDate);
        Assert.False(result.IsTriggered);
        Assert.Contains("Empty branch population", result.StatusNotes);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ConcurrentFinalBranchUpdates_DatabaseConstraintsEnforceSingleFinalReport()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, _, _, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        long testChatId = -100900000 - Random.Shared.Next(10000, 99999);
        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = testChatId,
            GroupTitle = "RFFO Final Broadcast",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });

        // Set both branches to Completed
        var b1 = await dbContext.Branches.SingleAsync(b => b.BranchCode == "36:01");
        var b2 = await dbContext.Branches.SingleAsync(b => b.BranchCode == "36:02");
        dbContext.ActivityStatuses.Add(new ActivityStatus
        {
            ActivityId = activity.ActivityId,
            BranchId = b1.BranchId,
            ReportingDate = TestDate,
            CurrentStatus = BranchActivityStatus.Completed,
            UpdatedAtUtc = TestUtcNow
        });
        dbContext.ActivityStatuses.Add(new ActivityStatus
        {
            ActivityId = activity.ActivityId,
            BranchId = b2.BranchId,
            ReportingDate = TestDate,
            CurrentStatus = BranchActivityStatus.Completed,
            UpdatedAtUtc = TestUtcNow
        });
        await dbContext.SaveChangesAsync();

        // 1. First execution triggers immediate final report and marks cycle Completed
        var firstResult = await scheduledService.ExecuteImmediateFinalReportAsync("RFFO", TestDate);
        Assert.True(firstResult.IsTriggered);
        Assert.False(firstResult.IsIdempotentRetry);
        Assert.Equal(ReportingCycleStatus.Completed, firstResult.CycleStatus);

        // 2. Second concurrent/retry execution detects cycle is already completed and returns idempotent result
        var secondResult = await scheduledService.ExecuteImmediateFinalReportAsync("RFFO", TestDate);
        Assert.True(secondResult.IsTriggered);
        Assert.True(secondResult.IsIdempotentRetry);
        Assert.Equal(firstResult.ReportId, secondResult.ReportId);
        Assert.Equal(ReportingCycleStatus.Completed, secondResult.CycleStatus);

        // Verify exactly one SummaryLog was created in the database for SEQ:1
        var finalKey = SummaryLog.BuildImmediateFinalIdempotencyKey(activity.ActivityId, TestDate, 1);
        var summaryLogs = await dbContext.SummaryLogs
            .Where(s => s.IdempotencyKey == finalKey)
            .ToListAsync();
        Assert.Single(summaryLogs);

        // 3. Directly verify SQL Server unique constraint UX_SummaryLog_IdempotencyKey via savepoint
        await dbContext.Database.CurrentTransaction!.CreateSavepointAsync("SummaryLogUniqueCheck");
        var winningSummary = summaryLogs[0];
        dbContext.SummaryLogs.Add(new SummaryLog
        {
            ReportingCycleId = winningSummary.ReportingCycleId,
            ActivityId = activity.ActivityId,
            ReportingDate = TestDate,
            GeneratedAtUtc = DateTime.UtcNow,
            TriggerType = ReportingTriggerType.ImmediateFinalAllCompleted,
            TotalActiveBranches = winningSummary.TotalActiveBranches,
            CompletedBranches = winningSummary.CompletedBranches,
            PendingBranches = winningSummary.PendingBranches,
            NotReportedBranches = winningSummary.NotReportedBranches,
            CompletionPercentage = winningSummary.CompletionPercentage,
            IdempotencyKey = finalKey
        });
        var summaryEx = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
        Assert.NotNull(summaryEx.InnerException);
        Assert.Contains("UX_SummaryLog_IdempotencyKey", summaryEx.InnerException.Message);
        await dbContext.Database.CurrentTransaction.RollbackToSavepointAsync("SummaryLogUniqueCheck");
        dbContext.ChangeTracker.Clear();

        // 4. Directly verify SQL Server unique constraint UX_ReportDeliveryLog_ReportId_DestinationGroupId via savepoint
        var existingDelivery = await dbContext.ReportDeliveryLogs.FirstAsync(d => d.ReportId == winningSummary.ReportId);
        await dbContext.Database.CurrentTransaction!.CreateSavepointAsync("DeliveryUniqueCheck");
        dbContext.ReportDeliveryLogs.Add(new ReportDeliveryLog
        {
            ReportId = winningSummary.ReportId,
            ActivityId = activity.ActivityId,
            ReportingDate = TestDate,
            DestinationGroupId = existingDelivery.DestinationGroupId,
            DestinationTelegramChatId = existingDelivery.DestinationTelegramChatId,
            IdempotencyKey = ReportDeliveryLog.BuildDeliveryIdempotencyKey(finalKey, existingDelivery.DestinationGroupId),
            DeliveryStatus = ReportDeliveryStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow
        });
        var deliveryEx = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
        Assert.NotNull(deliveryEx.InnerException);
        Assert.Contains("UX_ReportDeliveryLog_ReportId_DestinationGroupId", deliveryEx.InnerException.Message);
        await dbContext.Database.CurrentTransaction.RollbackToSavepointAsync("DeliveryUniqueCheck");
        dbContext.ChangeTracker.Clear();

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task FinalReportSnapshot_RemainsImmutable_EvenIfUnderlyingBranchDataChangesLater()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, processor, _, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100999011,
            GroupTitle = "RFFO Final Broadcast",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        // Complete both branches
        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:01", "Completed"),
            TelegramMessageLogId: 801,
            TelegramChatId: -100111,
            TelegramUserId: 501,
            SenderUsername: "user1",
            ExplicitReportingDate: TestDate));

        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:02", "Completed"),
            TelegramMessageLogId: 802,
            TelegramChatId: -100111,
            TelegramUserId: 502,
            SenderUsername: "user2",
            ExplicitReportingDate: TestDate));

        var finalKey = SummaryLog.BuildImmediateFinalIdempotencyKey(activity.ActivityId, TestDate, 1);
        var initialSnapshot = await dbContext.SummaryLogs.SingleAsync(s => s.IdempotencyKey == finalKey);
        var originalContent = initialSnapshot.FormattedReportContent;

        // Manually modify branch status in DB (e.g. database tampering or direct change)
        var b1 = await dbContext.Branches.SingleAsync(b => b.BranchCode == "36:01");
        var status1 = await dbContext.ActivityStatuses.SingleAsync(s => s.BranchId == b1.BranchId && s.ReportingDate == TestDate);
        status1.CurrentStatus = BranchActivityStatus.Pending;
        await dbContext.SaveChangesAsync();

        // Re-execute final report (retry): MUST return immutable persisted snapshot without re-aggregating!
        var retryResult = await scheduledService.ExecuteImmediateFinalReportAsync("RFFO", TestDate);
        Assert.True(retryResult.IsIdempotentRetry);

        var reloadedSnapshot = await dbContext.SummaryLogs.SingleAsync(s => s.IdempotencyKey == finalKey);
        Assert.Equal(originalContent, reloadedSnapshot.FormattedReportContent);
        Assert.Equal(2, reloadedSnapshot.CompletedBranches); // Remains 2 completed branches!

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task NoFutureSlotAvailable_DoesNotInventSlots()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        // Reopen after 18:00 (say 19:00 IST = 13:30 UTC)
        var lateUtcNow = new DateTime(2026, 11, 10, 13, 30, 0, DateTimeKind.Utc);
        var (orgService, _, scheduledService, processor, _, _) = CreateTestHarness(dbContext, lateUtcNow, TestDate);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100999012,
            GroupTitle = "RFFO Broadcast",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        // Ensure schedule (19 slots for 09:00-18:00)
        await scheduledService.DispatchDueReportsAsync(TestUtcNow);
        var initialSlots = await scheduledService.GetScheduledSlotsAsync("RFFO", TestDate);
        int initialCount = initialSlots.Count;

        // Complete both branches
        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:01", "Completed"),
            TelegramMessageLogId: 901,
            TelegramChatId: -100111,
            TelegramUserId: 501,
            SenderUsername: "user1",
            ExplicitReportingDate: TestDate));

        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:02", "Completed"),
            TelegramMessageLogId: 902,
            TelegramChatId: -100111,
            TelegramUserId: 502,
            SenderUsername: "user2",
            ExplicitReportingDate: TestDate));

        // Reopen at 19:00 IST
        var branch3601 = await dbContext.Branches.SingleAsync(b => b.BranchCode == "36:01");
        var permission = StatusReopenPermission.Grant(
            branchId: branch3601.BranchId,
            activityId: activity.ActivityId,
            reportingDate: TestDate,
            enabledBy: "admin.ops",
            reason: "Late audit correction",
            enabledAtUtc: lateUtcNow);

        dbContext.StatusReopenPermissions.Add(permission);
        await dbContext.SaveChangesAsync();

        var reopenUpdate = await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: Msg("36:01", "Pending"),
            TelegramMessageLogId: 903,
            TelegramChatId: -100111,
            TelegramUserId: 501,
            SenderUsername: "user1",
            ExplicitReportingDate: TestDate));

        Assert.True(reopenUpdate.IsAccepted);
        Assert.True(reopenUpdate.DidReactivateReportingCycle);

        // Verification: No new slot invented! Slot count remains initialCount (19)
        var slotsAfterReopen = await scheduledService.GetScheduledSlotsAsync("RFFO", TestDate);
        Assert.Equal(initialCount, slotsAfterReopen.Count);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task TelegramSecretMasker_ShouldMaskBotTokenInReportDeliveryLog()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, _, botClient, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100999013,
            GroupTitle = "RFFO Broadcast",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });

        // Complete both branches
        var b1 = await dbContext.Branches.SingleAsync(b => b.BranchCode == "36:01");
        var b2 = await dbContext.Branches.SingleAsync(b => b.BranchCode == "36:02");
        dbContext.ActivityStatuses.Add(new ActivityStatus
        {
            ActivityId = activity.ActivityId,
            BranchId = b1.BranchId,
            ReportingDate = TestDate,
            CurrentStatus = BranchActivityStatus.Completed,
            UpdatedAtUtc = TestUtcNow
        });
        dbContext.ActivityStatuses.Add(new ActivityStatus
        {
            ActivityId = activity.ActivityId,
            BranchId = b2.BranchId,
            ReportingDate = TestDate,
            CurrentStatus = BranchActivityStatus.Completed,
            UpdatedAtUtc = TestUtcNow
        });
        await dbContext.SaveChangesAsync();

        // Simulate Telegram error containing the bot token
        const string rawBotToken = "123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11";
        botClient.SetSendHandler((chatId, text, replyId) =>
            Task.FromResult(new TelegramBotSendResult(false, null, 401, $"Unauthorized request to https://api.telegram.org/bot{rawBotToken}/sendMessage")));

        var result = await scheduledService.ExecuteImmediateFinalReportAsync("RFFO", TestDate);
        Assert.True(result.IsTriggered);
        Assert.Equal(ReportDeliveryStatus.Failed, result.DeliveryStatus);

        var delivery = await dbContext.ReportDeliveryLogs.SingleAsync(d => d.ReportId == result.ReportId);
        Assert.NotNull(delivery.LastError);
        Assert.DoesNotContain(rawBotToken, delivery.LastError);
        Assert.Contains(TelegramSecretMasker.MaskedPlaceholder, delivery.LastError);

        await transaction.RollbackAsync();
    }

    private static ClaimsPrincipal CreatePrincipal(string username, string role)
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, role)
            },
            "TestAuth");

        return new ClaimsPrincipal(identity);
    }

    private sealed class FixedTestClock : IBusinessClock
    {
        private readonly DateOnly _businessDate;

        public FixedTestClock(DateTime utcNow, DateOnly businessDate)
        {
            UtcNow = utcNow;
            _businessDate = businessDate;
        }

        public DateTime UtcNow { get; }
        public DateOnly GetBusinessDate(string timeZoneId) => _businessDate;
        public DateTimeOffset ConvertUtcToTimeZone(DateTime utcDateTime, string timeZoneId) =>
            new(utcDateTime, TimeSpan.Zero);
    }

    private sealed class RecordingTestBotClient : ITelegramBotClient
    {
        private readonly List<SentTelegramMessageRecord> _sentMessages = new();
        private long _nextId = 1000;
        private Func<long, string, long?, Task<TelegramBotSendResult>>? _sendHandler;

        public IReadOnlyList<SentTelegramMessageRecord> SentMessages
        {
            get
            {
                lock (_sentMessages)
                {
                    return _sentMessages.ToList();
                }
            }
        }

        public void SetSendHandler(Func<long, string, long?, Task<TelegramBotSendResult>>? handler) =>
            _sendHandler = handler;

        public async Task<TelegramBotSendResult> SendMessageAsync(
            long chatId,
            string text,
            long? replyToMessageId = null,
            CancellationToken cancellationToken = default)
        {
            if (_sendHandler is not null)
            {
                return await _sendHandler(chatId, text, replyToMessageId);
            }

            var record = new SentTelegramMessageRecord
            {
                ChatId = chatId,
                Text = text,
                ReplyToMessageId = replyToMessageId,
                SentAtUtc = DateTime.UtcNow
            };

            lock (_sentMessages)
            {
                _sentMessages.Add(record);
            }

            var assignedId = Interlocked.Increment(ref _nextId);
            return new TelegramBotSendResult(true, assignedId, 200, null);
        }

        public Task<TelegramBotSendResult> SetWebhookAsync(string webhookUrl, string secretToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TelegramBotSendResult(true, null, 200, null));

        public Task<TelegramBotSendResult> DeleteWebhookAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TelegramBotSendResult(true, null, 200, null));

        public Task<TelegramWebhookInfoResult> GetWebhookInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TelegramWebhookInfoResult(true, 200, null, true, "url", false, 0, null, null));
    }

    private sealed class SentTelegramMessageRecord
    {
        public long ChatId { get; init; }
        public string Text { get; init; } = string.Empty;
        public long? ReplyToMessageId { get; init; }
        public DateTime SentAtUtc { get; init; }
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T currentValue) => CurrentValue = currentValue;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
