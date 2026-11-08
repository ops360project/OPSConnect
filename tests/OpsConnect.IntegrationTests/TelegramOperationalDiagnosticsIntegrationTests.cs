using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpsConnect.Api.Authentication;
using OpsConnect.Api.Controllers;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Common;
using OpsConnect.Application.Telegram;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Telegram;
using OpsConnect.Infrastructure.Persistence;
using OpsConnect.Infrastructure.Services;
using OpsConnect.Infrastructure.Telegram;

namespace OpsConnect.IntegrationTests;

public class TelegramOperationalDiagnosticsIntegrationTests
{
    private const string TestBotToken = "987654321:AAF_TestDiagnosticSecretBotToken_987654321";
    private const string TestSecretToken = "OpsConnect_Diagnostic_Secret_2026_SecureKey";
    private const long SourceGroupChatId = -100777888111;
    private const long InactiveSourceGroupChatId = -100777888222;
    private const long DestinationGroupChatId = -100777888333;

    private static OpsConnectDbContext CreateContext()
    {
        var factory = new OpsConnectDbContextFactory();
        return factory.CreateDbContext(Array.Empty<string>());
    }

    private static TelegramBotOptions CreateDefaultOptions() =>
        new()
        {
            IsEnabled = true,
            BotToken = TestBotToken,
            WebhookUrl = "https://opsconnect.example.com/api/telegram/webhook",
            WebhookSecretToken = TestSecretToken,
            MaxWebhookPayloadBytes = 65536,
            AuthorizedSourceChatIds = [SourceGroupChatId],
            DestinationChatIds = [DestinationGroupChatId]
        };

    private static ClaimsPrincipal CreateUser(string username, params string[] roles)
    {
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.Name, username));
        foreach (var role in roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }
        return new ClaimsPrincipal(identity);
    }

    private static TelegramWebhookController CreateController(
        OpsConnectDbContext context,
        TelegramBotOptions options,
        ITelegramBotClient? customBotClient = null,
        ClaimsPrincipal? user = null,
        IBusinessClock? clock = null)
    {
        var optionsMonitor = new StaticOptionsMonitor<TelegramBotOptions>(options);
        var effectiveClock = clock ?? new SystemBusinessClock();
        var botClient = customBotClient ?? new FakeDiagnosticBotClient();

        var normalizer = new TelegramUpdateNormalizer();
        var groupAuth = new TelegramGroupAuthorizationService(context, optionsMonitor);
        var rffoHandler = new RffoActivityUpdateHandler(context, effectiveClock);
        var activityProcessor = new ActivityUpdateProcessor(
            new TelegramActivityMessageParser(),
            new IActivityUpdateHandler[] { rffoHandler });

        var ingestionService = new TelegramMessageIngestionService(
            context,
            new TelegramActivityMessageParser(),
            activityProcessor,
            botClient,
            effectiveClock,
            optionsMonitor,
            NullLogger<TelegramMessageIngestionService>.Instance);

        var receiver = new TelegramWebhookReceiver(
            optionsMonitor,
            normalizer,
            groupAuth,
            ingestionService);

        var monitoringService = new TelegramOperationalMonitoringService(
            context,
            optionsMonitor,
            botClient,
            effectiveClock,
            NullLogger<TelegramOperationalMonitoringService>.Instance);

        var httpContext = new DefaultHttpContext
        {
            User = user ?? new ClaimsPrincipal(new ClaimsIdentity())
        };

        return new TelegramWebhookController(receiver, optionsMonitor, monitoringService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };
    }

    [Fact]
    public async Task HealthEndpoint_DatabaseHealthy_And_DatabaseUnhealthy_ShouldDistinguishStatuses()
    {
        var context = CreateContext();
        await using var tx = await context.Database.BeginTransactionAsync();

        var controller = CreateController(context, CreateDefaultOptions());

        // 1. Database is connected and healthy
        var healthyResp = await controller.GetHealth(verifyExternalApi: false);
        var healthyOk = Assert.IsType<OkObjectResult>(healthyResp.Result);
        var healthyReport = Assert.IsType<TelegramHealthReportDto>(healthyOk.Value);

        Assert.Equal(IntegrationHealthStatus.Healthy, healthyReport.Status);
        var dbComponent = Assert.Single(healthyReport.Components, c => c.ComponentName == "database");
        Assert.Equal(IntegrationHealthStatus.Healthy, dbComponent.Status);
        Assert.NotNull(dbComponent.LatencyMs);
        Assert.Null(dbComponent.Error);

        // 2. Simulated Database failure -> returns 503 Service Unavailable, marked Unhealthy, sanitized error
        var failingContext = new OpsConnectDbContext(new DbContextOptionsBuilder<OpsConnectDbContext>()
            .UseSqlServer("Server=127.0.0.1,9999;Database=NonExistent;User Id=sa;Password=WrongPassword123;Connect Timeout=1;")
            .Options);

        var failingController = CreateController(failingContext, CreateDefaultOptions());
        var unhealthyResp = await failingController.GetHealth(verifyExternalApi: false);
        var unhealthyObj = Assert.IsType<ObjectResult>(unhealthyResp.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unhealthyObj.StatusCode);

        var unhealthyReport = Assert.IsType<TelegramHealthReportDto>(unhealthyObj.Value);
        Assert.Equal(IntegrationHealthStatus.Unhealthy, unhealthyReport.Status);
        var failingDbComp = Assert.Single(unhealthyReport.Components, c => c.ComponentName == "database");
        Assert.Equal(IntegrationHealthStatus.Unhealthy, failingDbComp.Status);
        Assert.NotNull(failingDbComp.Error);
        Assert.DoesNotContain("WrongPassword123", failingDbComp.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HealthEndpoint_TelegramConfig_Missing_Invalid_And_Present_ShouldReturnExpectedCategories()
    {
        var context = CreateContext();
        await using var tx = await context.Database.BeginTransactionAsync();

        // 1. Missing / Placeholder bot token or disabled -> NotConfigured
        var disabledOptions = new TelegramBotOptions
        {
            IsEnabled = false,
            BotToken = "<SET_VIA_USER_SECRETS_OR_ENV>",
            WebhookUrl = "https://opsconnect.example.com/api/telegram/webhook",
            WebhookSecretToken = "<SET_VIA_USER_SECRETS_OR_ENV>"
        };

        var notConfiguredCtrl = CreateController(context, disabledOptions);
        var notConfiguredResp = await notConfiguredCtrl.GetHealth(verifyExternalApi: false);
        var notConfiguredOk = Assert.IsType<OkObjectResult>(notConfiguredResp.Result);
        var notConfiguredReport = Assert.IsType<TelegramHealthReportDto>(notConfiguredOk.Value);

        Assert.Equal(IntegrationHealthStatus.NotConfigured, notConfiguredReport.Status);
        var configComp = Assert.Single(notConfiguredReport.Components, c => c.ComponentName == "telegram_configuration");
        Assert.Equal(IntegrationHealthStatus.NotConfigured, configComp.Status);

        // 2. Invalid configuration (e.g. non-HTTPS webhook URL when enabled) -> Degraded
        var invalidOptions = new TelegramBotOptions
        {
            IsEnabled = true,
            BotToken = TestBotToken,
            WebhookUrl = "http://insecure-url.example.com/webhook",
            WebhookSecretToken = TestSecretToken
        };

        var invalidCtrl = CreateController(context, invalidOptions);
        var invalidResp = await invalidCtrl.GetHealth(verifyExternalApi: false);
        var invalidOk = Assert.IsType<OkObjectResult>(invalidResp.Result);
        var invalidReport = Assert.IsType<TelegramHealthReportDto>(invalidOk.Value);

        Assert.Equal(IntegrationHealthStatus.Degraded, invalidReport.Status);
        var invalidConfigComp = Assert.Single(invalidReport.Components, c => c.ComponentName == "telegram_configuration");
        Assert.Equal(IntegrationHealthStatus.Degraded, invalidConfigComp.Status);

        // 3. Fully valid configuration -> Healthy
        var validCtrl = CreateController(context, CreateDefaultOptions());
        var validResp = await validCtrl.GetHealth(verifyExternalApi: false);
        var validOk = Assert.IsType<OkObjectResult>(validResp.Result);
        var validReport = Assert.IsType<TelegramHealthReportDto>(validOk.Value);

        Assert.Equal(IntegrationHealthStatus.Healthy, validReport.Status);
    }

    [Fact]
    public async Task HealthEndpoint_TelegramApi_Available_And_Unavailable_ShouldNeverMarkAppOrDbUnhealthy()
    {
        var context = CreateContext();
        await using var tx = await context.Database.BeginTransactionAsync();

        var fakeBot = new FakeDiagnosticBotClient();

        // 1. External Telegram API verification succeeds
        fakeBot.WebhookInfoResult = new TelegramWebhookInfoResult(
            IsSuccess: true,
            HttpStatusCode: 200,
            ErrorMessage: null,
            IsConfigured: true,
            Url: "https://opsconnect.example.com/api/telegram/webhook",
            HasCustomCertificate: false,
            PendingUpdateCount: 3,
            LastErrorDateUtc: null,
            LastErrorMessage: null);

        var controller = CreateController(context, CreateDefaultOptions(), fakeBot);
        var successResp = await controller.GetHealth(verifyExternalApi: true);
        var successOk = Assert.IsType<OkObjectResult>(successResp.Result);
        var successReport = Assert.IsType<TelegramHealthReportDto>(successOk.Value);

        Assert.Equal(IntegrationHealthStatus.Healthy, successReport.Status);
        var apiCompSuccess = Assert.Single(successReport.Components, c => c.ComponentName == "telegram_api");
        Assert.Equal(IntegrationHealthStatus.Healthy, apiCompSuccess.Status);
        var webhookCompSuccess = Assert.Single(successReport.Components, c => c.ComponentName == "webhook");
        Assert.Equal(IntegrationHealthStatus.Healthy, webhookCompSuccess.Status);
        Assert.Contains("3", webhookCompSuccess.Description);

        // 2. External Telegram API verification fails (outage / timeout)
        fakeBot.WebhookInfoResult = new TelegramWebhookInfoResult(
            IsSuccess: false,
            HttpStatusCode: 502,
            ErrorMessage: "Bad Gateway returned from api.telegram.org",
            IsConfigured: false,
            Url: null,
            HasCustomCertificate: null,
            PendingUpdateCount: null,
            LastErrorDateUtc: null,
            LastErrorMessage: null);

        var outageResp = await controller.GetHealth(verifyExternalApi: true);
        // CRITICAL CHECK: HTTP Status is STILL 200 OK because the API & Database are operational!
        var outageOk = Assert.IsType<OkObjectResult>(outageResp.Result);
        var outageReport = Assert.IsType<TelegramHealthReportDto>(outageOk.Value);

        // Overall integration is Degraded, NOT Unhealthy!
        Assert.Equal(IntegrationHealthStatus.Degraded, outageReport.Status);

        // Database and API components remain Healthy!
        var apiComp = Assert.Single(outageReport.Components, c => c.ComponentName == "api");
        Assert.Equal(IntegrationHealthStatus.Healthy, apiComp.Status);
        var dbComp = Assert.Single(outageReport.Components, c => c.ComponentName == "database");
        Assert.Equal(IntegrationHealthStatus.Healthy, dbComp.Status);

        // Telegram components are Degraded
        var tgApiComp = Assert.Single(outageReport.Components, c => c.ComponentName == "telegram_api");
        Assert.Equal(IntegrationHealthStatus.Degraded, tgApiComp.Status);
        Assert.NotNull(tgApiComp.Error);
    }

    [Fact]
    public async Task OperationalStatus_ShouldReportCorrectGroupCounts_Timestamps_Metrics_AndStaleCounts()
    {
        var context = CreateContext();
        await using var tx = await context.Database.BeginTransactionAsync();

        var fixedClock = new FixedTestClock(
            new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc),
            new DateOnly(2026, 11, 8));

        var rffoActivity = await context.Activities.SingleAsync(a => a.ActivityCode == "RFFO");

        // 1. Seed active and inactive groups
        var activeSource1 = new TelegramGroup
        {
            ActivityId = rffoActivity.ActivityId,
            TelegramChatId = SourceGroupChatId,
            GroupTitle = "Active Operations Group 1",
            GroupRole = TelegramGroupRole.Source,
            IsActive = true,
            CreatedBy = "admin"
        };

        var activeSource2 = new TelegramGroup
        {
            ActivityId = rffoActivity.ActivityId,
            TelegramChatId = -100777888112,
            GroupTitle = "Active Operations Group 2",
            GroupRole = TelegramGroupRole.Source,
            IsActive = true,
            CreatedBy = "admin"
        };

        var inactiveSource = new TelegramGroup
        {
            ActivityId = rffoActivity.ActivityId,
            TelegramChatId = InactiveSourceGroupChatId,
            GroupTitle = "Decommissioned Group",
            GroupRole = TelegramGroupRole.Source,
            IsActive = false,
            CreatedBy = "admin"
        };

        var activeDest = new TelegramGroup
        {
            ActivityId = rffoActivity.ActivityId,
            TelegramChatId = DestinationGroupChatId,
            GroupTitle = "Broadcast Channel",
            GroupRole = TelegramGroupRole.Destination,
            IsActive = true,
            CreatedBy = "admin"
        };

        context.TelegramGroups.AddRange(activeSource1, activeSource2, inactiveSource, activeDest);

        // 2. Seed message logs with various statuses and timestamps
        var t0 = fixedClock.UtcNow.AddMinutes(-30);
        var t1 = fixedClock.UtcNow.AddMinutes(-20);
        var t2 = fixedClock.UtcNow.AddMinutes(-10);
        var tStale = fixedClock.UtcNow.AddMinutes(-15); // > 5 minutes ago

        var logs = new[]
        {
            // Message 1: Accepted, Delivered
            new TelegramMessageLog
            {
                TelegramUpdateId = 400001,
                TelegramChatId = SourceGroupChatId,
                TelegramMessageId = 1001,
                RawMessageText = "RFFO pending",
                ProcessingStatus = MessageProcessingStatus.Accepted,
                AcknowledgementStatus = AcknowledgementDeliveryStatus.Delivered,
                ReceivedAtUtc = t0,
                ProcessedAtUtc = t0.AddSeconds(2),
                AcknowledgementDeliveredAtUtc = t0.AddSeconds(4)
            },
            // Message 2: AcceptedUnchanged, Delivered
            new TelegramMessageLog
            {
                TelegramUpdateId = 400002,
                TelegramChatId = SourceGroupChatId,
                TelegramMessageId = 1002,
                RawMessageText = "RFFO unchanged",
                ProcessingStatus = MessageProcessingStatus.AcceptedUnchanged,
                AcknowledgementStatus = AcknowledgementDeliveryStatus.Delivered,
                ReceivedAtUtc = t1,
                ProcessedAtUtc = t1.AddSeconds(1),
                AcknowledgementDeliveredAtUtc = t1.AddSeconds(3)
            },
            // Message 3: RejectedInvalidFormat, Pending ack
            new TelegramMessageLog
            {
                TelegramUpdateId = 400003,
                TelegramChatId = SourceGroupChatId,
                TelegramMessageId = 1003,
                RawMessageText = "bad message",
                ProcessingStatus = MessageProcessingStatus.RejectedInvalidFormat,
                AcknowledgementStatus = AcknowledgementDeliveryStatus.Pending,
                ReceivedAtUtc = t2,
                ProcessedAtUtc = t2.AddSeconds(1)
            },
            // Message 4: RejectedUnknownBranch, FailedRetryable ack
            new TelegramMessageLog
            {
                TelegramUpdateId = 400004,
                TelegramChatId = SourceGroupChatId,
                TelegramMessageId = 1004,
                RawMessageText = "RFFO 99:99",
                ProcessingStatus = MessageProcessingStatus.RejectedUnknownBranch,
                AcknowledgementStatus = AcknowledgementDeliveryStatus.FailedRetryable,
                ReceivedAtUtc = t2.AddMinutes(1),
                ProcessedAtUtc = t2.AddMinutes(1).AddSeconds(1)
            },
            // Message 5: FailedError, FailedPermanent ack
            new TelegramMessageLog
            {
                TelegramUpdateId = 400005,
                TelegramChatId = SourceGroupChatId,
                TelegramMessageId = 1005,
                RawMessageText = "error payload",
                ProcessingStatus = MessageProcessingStatus.FailedError,
                AcknowledgementStatus = AcknowledgementDeliveryStatus.FailedPermanent,
                ReceivedAtUtc = t2.AddMinutes(2),
                ProcessedAtUtc = t2.AddMinutes(2).AddSeconds(1)
            },
            // Message 6: Stale Processing message (stuck in Processing for 15 minutes)
            new TelegramMessageLog
            {
                TelegramUpdateId = 400006,
                TelegramChatId = SourceGroupChatId,
                TelegramMessageId = 1006,
                RawMessageText = "stuck processing",
                ProcessingStatus = MessageProcessingStatus.Processing,
                AcknowledgementStatus = AcknowledgementDeliveryStatus.Pending,
                ReceivedAtUtc = tStale
            },
            // Message 7: Stale Sending acknowledgement (stuck in Sending for 15 minutes)
            new TelegramMessageLog
            {
                TelegramUpdateId = 400007,
                TelegramChatId = SourceGroupChatId,
                TelegramMessageId = 1007,
                RawMessageText = "stuck sending",
                ProcessingStatus = MessageProcessingStatus.Accepted,
                AcknowledgementStatus = AcknowledgementDeliveryStatus.Sending,
                ReceivedAtUtc = tStale,
                AcknowledgementSendingStartedAtUtc = tStale
            }
        };

        context.TelegramMessageLogs.AddRange(logs);
        await context.SaveChangesAsync();

        var adminUser = CreateUser("ops.admin", SystemRoles.Admin);
        var controller = CreateController(context, CreateDefaultOptions(), user: adminUser, clock: fixedClock);

        var statusResp = await controller.GetOperationalStatus(verifyExternalApi: false);
        var okResult = Assert.IsType<OkObjectResult>(statusResp.Result);
        var status = Assert.IsType<TelegramOperationalStatusDto>(okResult.Value);

        // Group counts
        Assert.Equal(2, status.GroupCounts.ActiveSourceGroupsCount);
        Assert.Equal(1, status.GroupCounts.ActiveDestinationGroupsCount);

        // Timestamps
        Assert.NotNull(status.RecentActivity.LastWebhookReceiptAtUtc);
        Assert.Equal(t2.AddMinutes(2), status.RecentActivity.LastWebhookReceiptAtUtc);
        Assert.Equal(t1.AddSeconds(1), status.RecentActivity.LastSuccessfullyProcessedAtUtc);
        Assert.Equal(t1.AddSeconds(3), status.RecentActivity.LastAcknowledgementDeliveredAtUtc);

        // Acknowledgement metrics
        Assert.Equal(2, status.AcknowledgementMetrics.PendingCount); // Message 3 and Message 6
        Assert.Equal(1, status.AcknowledgementMetrics.SendingCount); // Message 7
        Assert.Equal(1, status.AcknowledgementMetrics.FailedRetryableCount); // Message 4
        Assert.Equal(1, status.AcknowledgementMetrics.FailedPermanentCount); // Message 5
        Assert.Equal(2, status.AcknowledgementMetrics.TotalFailedCount); // 1 retryable + 1 permanent
        Assert.Equal(2, status.AcknowledgementMetrics.DeliveredCount); // Message 1 and Message 2

        // Stale metrics
        Assert.Equal(1, status.StaleMetrics.StaleProcessingMessageCount); // Message 6
        Assert.Equal(1, status.StaleMetrics.StaleSendingAcknowledgementCount); // Message 7
        Assert.Equal(5, status.StaleMetrics.StaleThresholdMinutes);
    }

    [Fact]
    public async Task DiagnosticsEndpoint_TimeWindowFiltering_AndSanitization()
    {
        var context = CreateContext();
        await using var tx = await context.Database.BeginTransactionAsync();

        var fixedClock = new FixedTestClock(
            new DateTime(2026, 11, 8, 14, 0, 0, DateTimeKind.Utc),
            new DateOnly(2026, 11, 8));

        var now = fixedClock.UtcNow;

        // Message inside 30-minute window (at now - 10m)
        var insideLog = new TelegramMessageLog
        {
            TelegramUpdateId = 500001,
            TelegramChatId = SourceGroupChatId,
            TelegramMessageId = 2001,
            SourceGroupTitle = "Operations Main",
            SenderUsername = "secret_user_private",
            SenderDisplayName = "Private User",
            RawMessageText = $"Top Secret Payload containing {TestBotToken}",
            ParsedActivityCode = "RFFO",
            ParsedBranchCode = "36:01",
            ProcessingStatus = MessageProcessingStatus.RejectedInvalidFormat,
            AcknowledgementStatus = AcknowledgementDeliveryStatus.FailedRetryable,
            RejectionReason = $"Invalid format near secret token {TestBotToken} and secret key {TestSecretToken}",
            ReceivedAtUtc = now.AddMinutes(-10),
            AcknowledgementAttemptCount = 2
        };

        // Message outside 30-minute window (at now - 50m)
        var outsideLog = new TelegramMessageLog
        {
            TelegramUpdateId = 500002,
            TelegramChatId = SourceGroupChatId,
            TelegramMessageId = 2002,
            SourceGroupTitle = "Operations Main",
            RawMessageText = "Old message",
            ProcessingStatus = MessageProcessingStatus.Accepted,
            AcknowledgementStatus = AcknowledgementDeliveryStatus.Delivered,
            ReceivedAtUtc = now.AddMinutes(-50)
        };

        context.TelegramMessageLogs.AddRange(insideLog, outsideLog);
        await context.SaveChangesAsync();

        var adminUser = CreateUser("ops.admin", SystemRoles.Admin);
        var controller = CreateController(context, CreateDefaultOptions(), user: adminUser, clock: fixedClock);

        // Query with 30-minute window
        var query = new TelegramOperationalDiagnosticsQuery(TimeWindowMinutes: 30, RecentErrorsLimit: 5);
        var diagResp = await controller.GetOperationalDiagnostics(query);
        var okResult = Assert.IsType<OkObjectResult>(diagResp.Result);
        var diagnostics = Assert.IsType<TelegramOperationalDiagnosticsDto>(okResult.Value);

        Assert.Equal(30, diagnostics.TimeWindowMinutes);
        Assert.Equal(1, diagnostics.Summary.TotalMessagesReceived); // Only insideLog!
        Assert.Equal(1, diagnostics.Summary.BusinessRejectedCount);
        Assert.Equal(1, diagnostics.Summary.PendingAcknowledgementRetriesCount);

        // Verify recent error sanitization
        var error = Assert.Single(diagnostics.RecentErrors);
        Assert.Equal(insideLog.TelegramMessageLogId, error.TelegramMessageLogId);
        Assert.Equal(SourceGroupChatId, error.TelegramChatId);
        Assert.Equal("Operations Main", error.SourceGroupTitle);
        Assert.Equal("RFFO", error.ParsedActivityCode);
        Assert.Equal("36:01", error.ParsedBranchCode);

        // CRITICAL CHECK: Secrets must NEVER appear in the sanitized error message!
        Assert.DoesNotContain(TestBotToken, error.SanitizedErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestSecretToken, error.SanitizedErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(TelegramSecretMasker.MaskedPlaceholder, error.SanitizedErrorMessage, StringComparison.Ordinal);

        // Entire response JSON serialization must NEVER leak secrets or raw message text
        var serialized = JsonSerializer.Serialize(diagnostics);
        Assert.DoesNotContain(TestBotToken, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestSecretToken, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Top Secret Payload", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret_user_private", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiagnosticsEndpoint_ShouldNeverMutateDatabase()
    {
        var context = CreateContext();
        await using var tx = await context.Database.BeginTransactionAsync();

        var fixedClock = new FixedTestClock(new DateTime(2026, 11, 8, 15, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 11, 8));

        var log = new TelegramMessageLog
        {
            TelegramUpdateId = 600001,
            TelegramChatId = SourceGroupChatId,
            TelegramMessageId = 3001,
            RawMessageText = "Sample",
            ProcessingStatus = MessageProcessingStatus.Accepted,
            AcknowledgementStatus = AcknowledgementDeliveryStatus.Delivered,
            ReceivedAtUtc = fixedClock.UtcNow
        };

        context.TelegramMessageLogs.Add(log);
        await context.SaveChangesAsync();

        var countBefore = await context.TelegramMessageLogs.CountAsync();
        var rowVersionBefore = log.RowVersion;

        var adminUser = CreateUser("ops.admin", SystemRoles.Admin);
        var controller = CreateController(context, CreateDefaultOptions(), user: adminUser, clock: fixedClock);

        // Execute all monitoring endpoints
        await controller.GetHealth(verifyExternalApi: false);
        await controller.GetOperationalStatus(verifyExternalApi: false);
        await controller.GetOperationalDiagnostics(new TelegramOperationalDiagnosticsQuery());

        var countAfter = await context.TelegramMessageLogs.CountAsync();
        await context.Entry(log).ReloadAsync();
        var rowVersionAfter = log.RowVersion;

        Assert.Equal(countBefore, countAfter);
        Assert.Equal(rowVersionBefore, rowVersionAfter);
    }

    [Fact]
    public async Task AuthenticationAndAuthorization_ShouldEnforceRoleRestrictions()
    {
        var context = CreateContext();
        await using var tx = await context.Database.BeginTransactionAsync();

        // 1. Health endpoint allows anonymous requests
        var anonCtrl = CreateController(context, CreateDefaultOptions(), user: null);
        var healthResp = await anonCtrl.GetHealth(verifyExternalApi: false);
        Assert.IsType<OkObjectResult>(healthResp.Result);

        // 2. Operational status requires authentication (rejects anonymous)
        var unauthIdentity = new ClaimsPrincipal(new ClaimsIdentity());
        var unauthCtrl = CreateController(context, CreateDefaultOptions(), user: unauthIdentity);
        var unauthStatusResp = await unauthCtrl.GetOperationalStatus(verifyExternalApi: false);
        Assert.IsType<UnauthorizedObjectResult>(unauthStatusResp.Result);

        // 3. Operational status allows Viewer, but Diagnostics forbids Viewer
        var viewerUser = CreateUser("viewer.user", SystemRoles.Viewer);
        var viewerCtrl = CreateController(context, CreateDefaultOptions(), user: viewerUser);
        var statusResp = await viewerCtrl.GetOperationalStatus(verifyExternalApi: false);
        Assert.IsType<OkObjectResult>(statusResp.Result);

        var viewerDiagResp = await viewerCtrl.GetOperationalDiagnostics(new TelegramOperationalDiagnosticsQuery());
        Assert.IsType<ForbidResult>(viewerDiagResp.Result);

        // 4. Diagnostics endpoint allows Admin and SuperAdmin
        var adminUser = CreateUser("admin.user", SystemRoles.Admin);
        var adminCtrl = CreateController(context, CreateDefaultOptions(), user: adminUser);
        var adminDiagResp = await adminCtrl.GetOperationalDiagnostics(new TelegramOperationalDiagnosticsQuery());
        Assert.IsType<OkObjectResult>(adminDiagResp.Result);

        var superAdminUser = CreateUser("super.admin", SystemRoles.SuperAdmin);
        var superAdminCtrl = CreateController(context, CreateDefaultOptions(), user: superAdminUser);
        var superDiagResp = await superAdminCtrl.GetOperationalDiagnostics(new TelegramOperationalDiagnosticsQuery());
        Assert.IsType<OkObjectResult>(superDiagResp.Result);
    }

    [Fact]
    public async Task OptionalConfigurationAbsent_ShouldHandleGracefully()
    {
        var context = CreateContext();
        await using var tx = await context.Database.BeginTransactionAsync();

        var emptyOptions = new TelegramBotOptions(); // default empty instance
        var controller = CreateController(context, emptyOptions);

        var healthResp = await controller.GetHealth(verifyExternalApi: false);
        var okResult = Assert.IsType<OkObjectResult>(healthResp.Result);
        var report = Assert.IsType<TelegramHealthReportDto>(okResult.Value);

        Assert.Equal(IntegrationHealthStatus.NotConfigured, report.Status);
    }

    private sealed class FakeDiagnosticBotClient : ITelegramBotClient
    {
        public TelegramWebhookInfoResult? WebhookInfoResult { get; set; }

        public Task<TelegramBotSendResult> SendMessageAsync(
            long chatId,
            string text,
            long? replyToMessageId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TelegramBotSendResult(true, 12345, 200, null));

        public Task<TelegramBotSendResult> SetWebhookAsync(
            string webhookUrl,
            string secretToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TelegramBotSendResult(true, null, 200, null));

        public Task<TelegramBotSendResult> DeleteWebhookAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TelegramBotSendResult(true, null, 200, null));

        public Task<TelegramWebhookInfoResult> GetWebhookInfoAsync(
            CancellationToken cancellationToken = default)
        {
            if (WebhookInfoResult is not null)
            {
                return Task.FromResult(WebhookInfoResult);
            }

            return Task.FromResult(new TelegramWebhookInfoResult(
                IsSuccess: true,
                HttpStatusCode: 200,
                ErrorMessage: null,
                IsConfigured: true,
                Url: "https://opsconnect.example.com/api/telegram/webhook",
                HasCustomCertificate: false,
                PendingUpdateCount: 0,
                LastErrorDateUtc: null,
                LastErrorMessage: null));
        }
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

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T currentValue) => CurrentValue = currentValue;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
