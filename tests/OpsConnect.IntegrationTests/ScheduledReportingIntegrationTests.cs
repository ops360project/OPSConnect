using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpsConnect.Api.Authentication;
using OpsConnect.Api.Controllers;
using OpsConnect.Application.Activities.Common;
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
using Xunit;

namespace OpsConnect.IntegrationTests;

public class ScheduledReportingIntegrationTests
{
    private static readonly DateOnly TestDate = new(2026, 11, 10);
    private static readonly DateTime TestUtcNow = new(2026, 11, 10, 3, 30, 0, DateTimeKind.Utc); // 09:00 IST

    private static OpsConnectDbContext CreateContext()
    {
        var factory = new OpsConnectDbContextFactory();
        return factory.CreateDbContext(Array.Empty<string>());
    }

    private static ClaimsPrincipal CreatePrincipal(string? username, string? role, bool isAuthenticated = true)
    {
        if (!isAuthenticated || string.IsNullOrWhiteSpace(username))
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username)
        };

        if (!string.IsNullOrWhiteSpace(role))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, OpsConnectAuthenticationHandler.SchemeName));
    }

    private static (
        IOrganisationMasterService OrgService,
        IActivityReportingService ReportingService,
        IScheduledReportingService ScheduledService,
        RecordingTestBotClient BotClient,
        ReportingController Controller)
        CreateTestHarness(
            OpsConnectDbContext dbContext,
            IBusinessClock? customClock = null,
            string? username = "admin.operator",
            string? role = SystemRoles.Admin,
            bool isAuthenticated = true)
    {
        var clock = customClock ?? new FixedTestClock(TestUtcNow, TestDate);

        var orgService = new OrganisationMasterService(
            dbContext,
            new OrganisationExcelReader(),
            new OrganisationHierarchyImportValidator(),
            clock);

        var reportingService = new ActivityReportingService(dbContext, clock);
        var formatter = new TelegramReportFormatter();
        var botClient = new RecordingTestBotClient();

        var botOptions = new StaticOptionsMonitor<TelegramBotOptions>(new TelegramBotOptions
        {
            IsEnabled = true,
            BotToken = "123456789:ABC-DEF1234ghIkl-zyx57W2v1u123ew11",
            WebhookSecretToken = "TestSecretToken12345678901234567890"
        });

        var scheduledService = new ScheduledReportingService(
            dbContext,
            reportingService,
            formatter,
            botClient,
            clock,
            botOptions,
            NullLogger<ScheduledReportingService>.Instance);

        var principal = CreatePrincipal(username, role, isAuthenticated);

        var controller = new ReportingController(reportingService, scheduledService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };

        return (orgService, reportingService, scheduledService, botClient, controller);
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

        // Ensure RFFO schedule exists for TestDate
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
    public async Task ScheduleAndCycleCreation_ShouldCreateExpectedDailySlots_RespectingConfiguredTimeZoneAndWindow()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, _, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var result = await scheduledService.DispatchDueReportsAsync(TestUtcNow);

        Assert.Equal(1, result.ActivitiesEvaluated);
        Assert.Equal(1, result.CyclesEnsured);

        var cycle = await scheduledService.GetReportingCycleStatusAsync("RFFO", TestDate);
        Assert.NotNull(cycle);
        Assert.Equal(ReportingCycleStatus.Active, cycle.CycleStatus);
        // 09:00 to 18:00 at 30 min intervals = 19 slots
        Assert.Equal(19, cycle.TotalSlots);

        // Ensure calling dispatch again does not create duplicate slots or cycles
        var repeatResult = await scheduledService.DispatchDueReportsAsync(TestUtcNow);
        var repeatCycle = await scheduledService.GetReportingCycleStatusAsync("RFFO", TestDate);
        Assert.NotNull(repeatCycle);
        Assert.Equal(19, repeatCycle.TotalSlots);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task DueSlotExecution_ShouldGenerateImmutableSnapshot_AndDeliverToDestinationGroup()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, botClient, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        // Add 1 Source group and 1 Destination group
        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100111222,
            GroupTitle = "RFFO Field Submissions",
            GroupRole = TelegramGroupRole.Source,
            IsActive = true
        });

        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100999888,
            GroupTitle = "RFFO Management Broadcast",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        // Dispatch at 09:00 IST (TestUtcNow) -> slot at 09:00 is due
        var dispatchResult = await scheduledService.DispatchDueReportsAsync(TestUtcNow);

        Assert.True(dispatchResult.SlotsClaimedAndProcessed >= 1);
        var executedSlot = Assert.Single(dispatchResult.ExecutedSlots);
        Assert.Equal(ReportDeliveryStatus.Delivered, executedSlot.Status);
        Assert.Equal(1, executedSlot.DestinationGroupsDelivered);

        // Verify sent message was delivered ONLY to Destination chat, NEVER to Source chat
        Assert.Single(botClient.SentMessages);
        var sentMsg = botClient.SentMessages[0];
        Assert.Equal(-100999888, sentMsg.ChatId);
        Assert.Contains("Request for Field Force Operations — Daily Status", sentMsg.Text);

        // Verify SummaryLog and ReportDeliveryLog persisted
        var slotKey = SummaryLog.BuildScheduledSlotIdempotencyKey(activity.ActivityId, TestDate, new TimeOnly(9, 0));
        var summary = await dbContext.SummaryLogs.Include(s => s.Deliveries).SingleAsync(s => s.IdempotencyKey == slotKey);
        Assert.Equal(2, summary.TotalActiveBranches);
        Assert.Single(summary.Deliveries);
        Assert.Equal(ReportDeliveryStatus.Delivered, summary.Deliveries.First().DeliveryStatus);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task DestinationGroupValidation_MustNeverSendToSourceGroup()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, botClient, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        // ONLY Source groups configured (no destination group)
        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100111222,
            GroupTitle = "Source Submissions Only",
            GroupRole = TelegramGroupRole.Source,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        var result = await scheduledService.DispatchDueReportsAsync(TestUtcNow);

        Assert.Equal(1, result.SlotsFailed);
        var executed = Assert.Single(result.ExecutedSlots);
        Assert.Equal(ReportDeliveryStatus.Failed, executed.Status);
        Assert.Contains("No active Telegram destination group configured", executed.StatusNotes);

        // Absolutely ZERO messages sent to Telegram
        Assert.Empty(botClient.SentMessages);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task IdempotentRetry_ShouldPreserveImmutableSnapshot_WhenUnderlyingBranchStatusChanges()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, botClient, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100999888,
            GroupTitle = "Management",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        // 1. Initial dispatch at 09:00: both branches NotReported -> Completed: 0, Total: 2, 0.00%
        var dispatch1 = await scheduledService.DispatchDueReportsAsync(TestUtcNow);
        var slot1 = Assert.Single(dispatch1.ExecutedSlots);
        Assert.Equal(0, slot1.CompletedBranches);

        // 2. Later, branch 36:01 is submitted as Completed
        var b3601 = await dbContext.Branches.SingleAsync(b => b.BranchCode == "36:01");
        dbContext.ActivityStatuses.Add(new ActivityStatus
        {
            ActivityId = activity.ActivityId,
            BranchId = b3601.BranchId,
            ReportingDate = TestDate,
            CurrentStatus = BranchActivityStatus.Completed
        });
        await dbContext.SaveChangesAsync();

        // 3. Re-execute the same slot (simulating Hangfire retry)
        int sentCountBefore = botClient.SentMessages.Count;
        var retryResult = await scheduledService.ExecuteSlotAsync(slot1.ReportingSlotId);

        // Verification: It is an idempotent retry
        Assert.True(retryResult.IsIdempotentRetry);
        Assert.Equal(ReportDeliveryStatus.Delivered, retryResult.Status);
        // The snapshot must remain IMMUTABLE: CompletedBranches is still 0 from the original snapshot!
        Assert.Equal(0, retryResult.CompletedBranches);
        // Does not re-send to already confirmed destination group
        Assert.Equal(sentCountBefore, botClient.SentMessages.Count);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task SafeCatchUpPolicy_ShouldExpireSlotsOlderThanCatchUpWindow()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, botClient, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100999888,
            GroupTitle = "Management",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        // Simulate application was down until 15:00 IST (9:30 UTC).
        // A slot at 09:00 IST is now 6 hours old (> 4-hour catch up window).
        var lateNowUtc = TestUtcNow.AddHours(6); // 15:00 IST

        var result = await scheduledService.DispatchDueReportsAsync(lateNowUtc);

        // Old slots exceeding 4-hour catch-up window are expired/failed with notes
        var slots = await scheduledService.GetScheduledSlotsAsync("RFFO", TestDate);
        var slot0900 = Assert.Single(slots, s => s.ScheduledSlotTime == new TimeOnly(9, 0));

        Assert.Equal(ReportDeliveryStatus.Failed, slot0900.SlotStatus);
        Assert.Contains("exceeding safe catch-up window", slot0900.StatusNotes);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task StaleProcessingRecovery_ShouldRecoverSlotsAbandonedByCrashedWorker()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, botClient, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100999888,
            GroupTitle = "Management",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        // 1. First dispatch creates slots
        await scheduledService.DispatchDueReportsAsync(TestUtcNow);

        var slots = await scheduledService.GetScheduledSlotsAsync("RFFO", TestDate);
        var slot0900 = await dbContext.ReportingSlots.SingleAsync(s => s.ReportingSlotId == slots[0].ReportingSlotId);

        // 2. Simulate worker crash: slot is left in Processing with UpdatedAtUtc 15 minutes ago
        slot0900.SlotStatus = ReportDeliveryStatus.Processing;
        slot0900.UpdatedAtUtc = TestUtcNow.AddMinutes(-15);
        await dbContext.SaveChangesAsync();

        // 3. Next dispatch tick recovers the stale slot
        var recoveryResult = await scheduledService.DispatchDueReportsAsync(TestUtcNow);
        Assert.True(recoveryResult.SlotsClaimedAndProcessed >= 1);

        var updatedSlot = await dbContext.ReportingSlots.SingleAsync(s => s.ReportingSlotId == slot0900.ReportingSlotId);
        Assert.Equal(ReportDeliveryStatus.Delivered, updatedSlot.SlotStatus);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task MultipartMessageOrdering_And_PartialDeliveryRecovery()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, botClient, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100999888,
            GroupTitle = "Management",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        // Ensure slots exist
        await scheduledService.DispatchDueReportsAsync(TestUtcNow);
        var slots = await scheduledService.GetScheduledSlotsAsync("RFFO", TestDate);
        // Use slot 1 (09:30) which hasn't been executed
        var slot0930 = slots[1];
        var slotId = slot0930.ReportingSlotId;
        var slotEntity = await dbContext.ReportingSlots.SingleAsync(s => s.ReportingSlotId == slotId);

        // Script bot client: Part 1 succeeds (returns msg ID 5555), Part 2 fails with transient error
        int callCount = 0;
        botClient.SetSendHandler((chatId, text, replyId) =>
        {
            callCount++;
            if (callCount == 1)
            {
                return Task.FromResult(new TelegramBotSendResult(true, 5555, 200, null));
            }

            return Task.FromResult(new TelegramBotSendResult(false, null, 500, "Simulated transient Telegram 500 error"));
        });

        // Manually insert an immutable 2-part report for slot 09:30 to test multipart partial delivery recovery
        var idempotencyKey = SummaryLog.BuildScheduledSlotIdempotencyKey(activity.ActivityId, TestDate, slot0930.ScheduledSlotTime);
        var summary = new SummaryLog
        {
            ReportingCycleId = slotEntity.ReportingCycleId,
            ActivityId = activity.ActivityId,
            ReportingDate = TestDate,
            TriggerType = ReportingTriggerType.ScheduledSlot,
            ScheduledSlotTime = slot0930.ScheduledSlotTime,
            IdempotencyKey = idempotencyKey,
            TotalActiveBranches = 2,
            CompletedBranches = 0,
            PendingBranches = 0,
            NotReportedBranches = 2,
            CompletionPercentage = 0.00m,
            FormattedReportContent = "Part 1 Content\n\n---\n\nPart 2 Content",
            GeneratedAtUtc = TestUtcNow
        };
        dbContext.SummaryLogs.Add(summary);
        await dbContext.SaveChangesAsync();

        // Execute slot: Part 1 succeeds, Part 2 fails
        var firstExec = await scheduledService.ExecuteSlotAsync(slotId);
        Assert.Equal(ReportDeliveryStatus.Failed, firstExec.Status);

        // Verify partial delivery state: Part 1 ID (5555) is recorded
        var delivery = await dbContext.ReportDeliveryLogs.SingleAsync(d => d.ReportId == summary.ReportId);
        Assert.Equal("5555", delivery.DeliveredTelegramMessageIds);
        Assert.Equal(ReportDeliveryStatus.Failed, delivery.DeliveryStatus);

        // Reset bot client to succeed on all subsequent calls
        botClient.SetSendHandler((chatId, text, replyId) =>
            Task.FromResult(new TelegramBotSendResult(true, 5556, 200, null)));

        // Retry slot execution: should resume from Part 2 only (not resend Part 1!)
        var secondExec = await scheduledService.ExecuteSlotAsync(slotId);
        Assert.Equal(ReportDeliveryStatus.Delivered, secondExec.Status);

        // Delivery log now has both confirmed message IDs: 5555,5556
        var updatedDelivery = await dbContext.ReportDeliveryLogs.SingleAsync(d => d.ReportId == summary.ReportId);
        Assert.Equal("5555,5556", updatedDelivery.DeliveredTelegramMessageIds);
        Assert.Equal(ReportDeliveryStatus.Delivered, updatedDelivery.DeliveryStatus);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task TelegramFailure_ShouldRecordSanitizedErrorsWithoutLeakingSecrets()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, botClient, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == "RFFO");
        await ClearExistingDestinationGroupsAsync(dbContext, activity.ActivityId);

        dbContext.TelegramGroups.Add(new TelegramGroup
        {
            ActivityId = activity.ActivityId,
            TelegramChatId = -100999888,
            GroupTitle = "Management",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        // Bot client returns error containing raw BotToken
        string rawSecret = "123456789:ABC-DEF1234ghIkl-zyx57W2v1u123ew11";
        botClient.SetSendHandler((chatId, text, replyId) =>
            Task.FromResult(new TelegramBotSendResult(false, null, 400, $"Bad Request: token {rawSecret} rejected by gateway.")));

        var result = await scheduledService.DispatchDueReportsAsync(TestUtcNow);
        var failedSlot = Assert.Single(result.ExecutedSlots);
        Assert.Equal(ReportDeliveryStatus.Failed, failedSlot.Status);

        // Verify that raw secret was sanitized and never stored in LastError
        var delivery = await dbContext.ReportDeliveryLogs.FirstAsync(d => d.DestinationGroupId == -100999888 || d.DestinationTelegramChatId == -100999888);
        Assert.DoesNotContain(rawSecret, delivery.LastError);
        Assert.Contains("***MASKED***", delivery.LastError);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ApiEndpoints_Schedule_CycleStatus_And_Dispatch_ShouldEnforceSecurityAndReturnData()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, scheduledService, _, _) = CreateTestHarness(dbContext);
        await SeedHierarchyAndActivityAsync(orgService, dbContext, TestDate);

        // 1. Viewer role can read schedule and cycle status
        var viewerController = new ReportingController(
            new ActivityReportingService(dbContext, new FixedTestClock(TestUtcNow, TestDate)),
            scheduledService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal("viewer.ops", SystemRoles.Viewer) }
            }
        };

        var scheduleResult = await viewerController.GetSchedule("RFFO", reportingDate: TestDate);
        var okSchedule = Assert.IsType<OkObjectResult>(scheduleResult.Result);
        var slots = Assert.IsAssignableFrom<IReadOnlyList<ScheduledSlotStatusDto>>(okSchedule.Value);

        // 2. Viewer role is FORBIDDEN on POST dispatch
        var dispatchForbiddenResult = await viewerController.TriggerDispatch("RFFO");
        Assert.IsType<ForbidResult>(dispatchForbiddenResult.Result);

        // 3. Admin role is ALLOWED on POST dispatch
        var adminController = new ReportingController(
            new ActivityReportingService(dbContext, new FixedTestClock(TestUtcNow, TestDate)),
            scheduledService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal("admin.ops", SystemRoles.Admin) }
            }
        };

        var dispatchAllowedResult = await adminController.TriggerDispatch("RFFO");
        var okDispatch = Assert.IsType<OkObjectResult>(dispatchAllowedResult.Result);
        var dispatchData = Assert.IsType<ScheduledReportingDispatchResult>(okDispatch.Value);
        Assert.Equal(1, dispatchData.ActivitiesEvaluated);

        await transaction.RollbackAsync();
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
