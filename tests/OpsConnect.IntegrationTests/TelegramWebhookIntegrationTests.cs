using System.Security.Claims;
using System.Text;
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
using OpsConnect.Infrastructure.Telegram;

namespace OpsConnect.IntegrationTests;

public class TelegramWebhookIntegrationTests
{
    private const string TestSecretToken = "OpsConnect_Test_WebhookSecret_2026_Secure";
    private const string TestBotToken = "987654321:AAF_TestSecretBotTokenValue_987654321";
    private const long ConfiguredSourceChatId = -100555000111;
    private const long DbSourceChatId = -100555000222;
    private const long InactiveDbGroupChatId = -100555000333;
    private const long UnauthorizedChatId = -100999888777;

    private static OpsConnectDbContext CreateContext()
    {
        var factory = new OpsConnectDbContextFactory();
        return factory.CreateDbContext(Array.Empty<string>());
    }

    private static TelegramBotOptions CreateDefaultOptions(int maxPayloadBytes = 65536) =>
        new()
        {
            IsEnabled = true,
            BotToken = TestBotToken,
            WebhookUrl = "https://opsconnect.example.com/api/telegram/webhook",
            WebhookSecretToken = TestSecretToken,
            MaxWebhookPayloadBytes = maxPayloadBytes,
            AuthorizedSourceChatIds = [ConfiguredSourceChatId, InactiveDbGroupChatId],
            DestinationChatIds = [-100555000999]
        };

    private static TelegramWebhookController CreateWebhookController(
        OpsConnectDbContext dbContext,
        TelegramBotOptions options,
        string? secretHeaderValue,
        byte[] bodyBytes,
        ClaimsPrincipal? user = null,
        IBusinessClock? clock = null,
        ITelegramMessageIngestionService? customIngestionService = null)
    {
        var optionsMonitor = new StaticOptionsMonitor<TelegramBotOptions>(options);
        var normalizer = new TelegramUpdateNormalizer();
        var groupAuthService = new TelegramGroupAuthorizationService(dbContext, optionsMonitor);
        var effectiveClock = clock ?? new SystemBusinessClock();
        var ingestionService = customIngestionService ?? new TelegramMessageIngestionService(
            dbContext,
            new TelegramActivityMessageParser(),
            effectiveClock,
            optionsMonitor,
            NullLogger<TelegramMessageIngestionService>.Instance);

        var receiver = new TelegramWebhookReceiver(
            optionsMonitor,
            normalizer,
            groupAuthService,
            ingestionService);

        var httpContext = new DefaultHttpContext
        {
            User = user ?? new ClaimsPrincipal(new ClaimsIdentity())
        };

        if (secretHeaderValue is not null)
        {
            httpContext.Request.Headers[TelegramBotOptions.SecretTokenHeaderName] = secretHeaderValue;
        }

        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.ContentType = "application/json";

        return new TelegramWebhookController(receiver, optionsMonitor)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };
    }

    private static byte[] SerializeUpdate(object payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload);

    [Fact]
    public async Task WebhookEndpoint_ShouldPersistAuthorizedMessages_EnforcePrecedence_HandleDuplicates_AndNotChangeRffoStatus()
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

            var fixedReceivedUtc = new DateTime(2026, 11, 8, 7, 30, 0, DateTimeKind.Utc);
            var fixedClock = new FixedTestClock(fixedReceivedUtc, new DateOnly(2026, 11, 8));
            var rffoActivity = await context.Activities.SingleAsync(a => a.ActivityCode == "RFFO");

            // 1. Seed an active DB Source Group and an inactive DB Source Group
            // Note: InactiveDbGroupChatId IS present in TelegramBotOptions.AuthorizedSourceChatIds,
            // so this directly verifies that an inactive DB group overrides stale static configuration!
            var activeDbGroup = new TelegramGroup
            {
                ActivityId = rffoActivity.ActivityId,
                TelegramChatId = DbSourceChatId,
                GroupTitle = "DB Authorised RFFO Source Group",
                GroupRole = TelegramGroupRole.Source,
                IsActive = true,
                CreatedBy = "test.admin"
            };

            var inactiveDbGroup = new TelegramGroup
            {
                ActivityId = rffoActivity.ActivityId,
                TelegramChatId = InactiveDbGroupChatId,
                GroupTitle = "Deactivated Legacy Source Group",
                GroupRole = TelegramGroupRole.Source,
                IsActive = false,
                CreatedBy = "test.admin"
            };

            context.TelegramGroups.AddRange(activeDbGroup, inactiveDbGroup);
            await context.SaveChangesAsync();

            var validGroupMessageBytes = SerializeUpdate(new
            {
                update_id = 100001,
                message = new
                {
                    message_id = 501,
                    date = 1762580000,
                    text = "Activity : RFFO\nBranch Code : 36:01\nStatus : Pending",
                    chat = new
                    {
                        id = DbSourceChatId,
                        type = "supergroup",
                        title = "Telegram Payload Title"
                    },
                    from = new
                    {
                        id = 900123,
                        is_bot = false,
                        first_name = "Suresh",
                        last_name = "Nayak",
                        username = "suresh_3601"
                    }
                }
            });

            // 2. Missing & invalid webhook secrets -> 401 Unauthorized, 0 rows persisted
            var missingSecretCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(),
                secretHeaderValue: null,
                bodyBytes: validGroupMessageBytes,
                clock: fixedClock);
            var missingResp = await missingSecretCtrl.ReceiveWebhook();
            Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<ObjectResult>(missingResp.Result).StatusCode);

            var invalidSecretCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(),
                secretHeaderValue: "Wrong_Secret_Token_Value_0000",
                bodyBytes: validGroupMessageBytes,
                clock: fixedClock);
            var invalidResp = await invalidSecretCtrl.ReceiveWebhook();
            Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<ObjectResult>(invalidResp.Result).StatusCode);
            Assert.Equal(0, await context.TelegramMessageLogs.CountAsync());

            // 3. Oversized & malformed payloads -> 413 / 400, 0 rows persisted
            var oversizedBytes = Encoding.UTF8.GetBytes("{\"update_id\":1,\"padding\":\"" + new string('A', 2048) + "\"}");
            var oversizedCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(maxPayloadBytes: 1024),
                secretHeaderValue: TestSecretToken,
                bodyBytes: oversizedBytes,
                clock: fixedClock);
            Assert.Equal(StatusCodes.Status413PayloadTooLarge, Assert.IsType<ObjectResult>((await oversizedCtrl.ReceiveWebhook()).Result).StatusCode);

            var malformedCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(),
                secretHeaderValue: TestSecretToken,
                bodyBytes: Encoding.UTF8.GetBytes("{not-valid-json"),
                clock: fixedClock);
            Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ObjectResult>((await malformedCtrl.ReceiveWebhook()).Result).StatusCode);

            // 4. Unsupported media and service messages -> 200 OK (IgnoredNonMessageUpdate), 0 rows persisted
            var mediaMessageBytes = SerializeUpdate(new
            {
                update_id = 100002,
                message = new
                {
                    message_id = 502,
                    date = 1762580000,
                    caption = "Photo without text field",
                    chat = new { id = DbSourceChatId, type = "supergroup" },
                    from = new { id = 900123, is_bot = false, first_name = "Suresh" }
                }
            });
            var mediaCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(),
                secretHeaderValue: TestSecretToken,
                bodyBytes: mediaMessageBytes,
                clock: fixedClock);
            var mediaObj = Assert.IsType<ObjectResult>((await mediaCtrl.ReceiveWebhook()).Result);
            Assert.Equal(StatusCodes.Status200OK, mediaObj.StatusCode);
            Assert.Equal(
                TelegramUpdateHandlingOutcome.IgnoredNonMessageUpdate,
                Assert.IsType<TelegramWebhookHandlingResult>(mediaObj.Value).Outcome);
            Assert.Equal(0, await context.TelegramMessageLogs.CountAsync());

            // 5. Unauthorised group message -> 403 Forbidden, persisted with RejectedUnauthorisedGroup & RejectionReason
            var unauthorizedGroupBytes = SerializeUpdate(new
            {
                update_id = 100003,
                message = new
                {
                    message_id = 503,
                    date = 1762580000,
                    text = "Activity : RFFO\nBranch Code : 36:01\nStatus : Pending",
                    chat = new { id = UnauthorizedChatId, type = "supergroup", title = "Random Unauthorised Group" },
                    from = new { id = 900123, is_bot = false, first_name = "Suresh" }
                }
            });
            var unauthorizedGroupCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(),
                secretHeaderValue: TestSecretToken,
                bodyBytes: unauthorizedGroupBytes,
                clock: fixedClock);
            var unauthGroupObj = Assert.IsType<ObjectResult>((await unauthorizedGroupCtrl.ReceiveWebhook()).Result);
            Assert.Equal(StatusCodes.Status403Forbidden, unauthGroupObj.StatusCode);
            var unauthGroupPayload = Assert.IsType<TelegramWebhookHandlingResult>(unauthGroupObj.Value);
            Assert.Equal(TelegramUpdateHandlingOutcome.RejectedUnauthorizedSourceGroup, unauthGroupPayload.Outcome);
            Assert.NotNull(unauthGroupPayload.TelegramMessageLogId);

            var persistedUnauthLog = await context.TelegramMessageLogs
                .AsNoTracking()
                .SingleAsync(x => x.TelegramMessageLogId == unauthGroupPayload.TelegramMessageLogId);
            Assert.Equal(MessageProcessingStatus.RejectedUnauthorisedGroup, persistedUnauthLog.ProcessingStatus);
            Assert.NotNull(persistedUnauthLog.RejectionReason);

            // 6. Inactive DB group (even though Chat ID is in AuthorizedSourceChatIds!) -> 403 Forbidden (Database-First Precedence)
            var inactiveGroupBytes = SerializeUpdate(new
            {
                update_id = 100004,
                message = new
                {
                    message_id = 504,
                    date = 1762580000,
                    text = "Activity : RFFO\nBranch Code : 36:01\nStatus : Pending",
                    chat = new { id = InactiveDbGroupChatId, type = "supergroup", title = "Deactivated Legacy Source Group" },
                    from = new { id = 900123, is_bot = false, first_name = "Suresh" }
                }
            });
            var inactiveGroupCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(),
                secretHeaderValue: TestSecretToken,
                bodyBytes: inactiveGroupBytes,
                clock: fixedClock);
            var inactiveGroupObj = Assert.IsType<ObjectResult>((await inactiveGroupCtrl.ReceiveWebhook()).Result);
            Assert.Equal(StatusCodes.Status403Forbidden, inactiveGroupObj.StatusCode);
            var inactiveGroupPayload = Assert.IsType<TelegramWebhookHandlingResult>(inactiveGroupObj.Value);
            Assert.Equal(TelegramUpdateHandlingOutcome.RejectedUnauthorizedSourceGroup, inactiveGroupPayload.Outcome);
            Assert.Contains("inactive", inactiveGroupPayload.Message, StringComparison.OrdinalIgnoreCase);

            // 7. New authorised group message -> 200 OK (AcceptedForProcessing), durably persisted with all audit fields
            var validDbGroupCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(),
                secretHeaderValue: TestSecretToken,
                bodyBytes: validGroupMessageBytes,
                clock: fixedClock);
            var validDbObj = Assert.IsType<ObjectResult>((await validDbGroupCtrl.ReceiveWebhook()).Result);
            Assert.Equal(StatusCodes.Status200OK, validDbObj.StatusCode);
            var validDbPayload = Assert.IsType<TelegramWebhookHandlingResult>(validDbObj.Value);
            Assert.True(validDbPayload.IsSuccess);
            Assert.Equal(TelegramUpdateHandlingOutcome.AcceptedForProcessing, validDbPayload.Outcome);
            Assert.NotNull(validDbPayload.TelegramMessageLogId);
            Assert.Equal(MessageProcessingStatus.Received, validDbPayload.PersistedProcessingStatus);

            var persistedAuthorizedLog = await context.TelegramMessageLogs
                .AsNoTracking()
                .SingleAsync(x => x.TelegramMessageLogId == validDbPayload.TelegramMessageLogId);

            Assert.Equal(100001, persistedAuthorizedLog.TelegramUpdateId);
            Assert.Equal(DbSourceChatId, persistedAuthorizedLog.TelegramChatId);
            Assert.Equal(501, persistedAuthorizedLog.TelegramMessageId);
            Assert.Equal(activeDbGroup.TelegramGroupId, persistedAuthorizedLog.SourceTelegramGroupId);
            Assert.Equal("DB Authorised RFFO Source Group", persistedAuthorizedLog.SourceGroupTitle);
            Assert.Equal(900123, persistedAuthorizedLog.TelegramUserId);
            Assert.Equal("suresh_3601", persistedAuthorizedLog.SenderUsername);
            Assert.Equal("Suresh Nayak", persistedAuthorizedLog.SenderDisplayName);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1762580000).UtcDateTime, persistedAuthorizedLog.MessageTimestampUtc);
            Assert.Equal(fixedReceivedUtc, persistedAuthorizedLog.ReceivedAtUtc);
            Assert.Equal("Activity : RFFO\nBranch Code : 36:01\nStatus : Pending", persistedAuthorizedLog.RawMessageText);
            Assert.Equal("RFFO", persistedAuthorizedLog.ParsedActivityCode);
            Assert.Equal("36:01", persistedAuthorizedLog.ParsedBranchCode);
            Assert.Equal("Pending", persistedAuthorizedLog.ParsedStatusText);
            Assert.Equal(rffoActivity.ActivityId, persistedAuthorizedLog.ActivityId);
            Assert.Equal(MessageProcessingStatus.Received, persistedAuthorizedLog.ProcessingStatus);

            // 8. Repeated Telegram Update ID (100001) -> 200 OK (DuplicateIgnored), preserves original row
            var laterClock = new FixedTestClock(fixedReceivedUtc.AddMinutes(5), new DateOnly(2026, 11, 8));
            var duplicateUpdateIdBytes = SerializeUpdate(new
            {
                update_id = 100001,
                message = new
                {
                    message_id = 999,
                    date = 1762580300,
                    text = "Activity : RFFO\nBranch Code : 36:01\nStatus : Completed",
                    chat = new { id = DbSourceChatId, type = "supergroup" },
                    from = new { id = 900123, is_bot = false, first_name = "Suresh" }
                }
            });
            var dupUpdateIdCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(),
                secretHeaderValue: TestSecretToken,
                bodyBytes: duplicateUpdateIdBytes,
                clock: laterClock);
            var dupUpdateIdObj = Assert.IsType<ObjectResult>((await dupUpdateIdCtrl.ReceiveWebhook()).Result);
            Assert.Equal(StatusCodes.Status200OK, dupUpdateIdObj.StatusCode);
            var dupUpdateIdPayload = Assert.IsType<TelegramWebhookHandlingResult>(dupUpdateIdObj.Value);
            Assert.Equal(TelegramUpdateHandlingOutcome.DuplicateIgnored, dupUpdateIdPayload.Outcome);
            Assert.Equal(validDbPayload.TelegramMessageLogId, dupUpdateIdPayload.TelegramMessageLogId);

            // 9. Repeated Chat ID + Message ID (DbSourceChatId + 501) with a new Update ID -> 200 OK (DuplicateIgnored)
            var duplicateChatMessageBytes = SerializeUpdate(new
            {
                update_id = 100099,
                message = new
                {
                    message_id = 501,
                    date = 1762580300,
                    text = "Activity : RFFO\nBranch Code : 36:01\nStatus : Completed",
                    chat = new { id = DbSourceChatId, type = "supergroup" },
                    from = new { id = 900123, is_bot = false, first_name = "Suresh" }
                }
            });
            var dupChatMsgCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(),
                secretHeaderValue: TestSecretToken,
                bodyBytes: duplicateChatMessageBytes,
                clock: laterClock);
            var dupChatMsgObj = Assert.IsType<ObjectResult>((await dupChatMsgCtrl.ReceiveWebhook()).Result);
            Assert.Equal(StatusCodes.Status200OK, dupChatMsgObj.StatusCode);
            var dupChatMsgPayload = Assert.IsType<TelegramWebhookHandlingResult>(dupChatMsgObj.Value);
            Assert.Equal(TelegramUpdateHandlingOutcome.DuplicateIgnored, dupChatMsgPayload.Outcome);
            Assert.Equal(validDbPayload.TelegramMessageLogId, dupChatMsgPayload.TelegramMessageLogId);

            // Verify original message record was NOT overwritten by duplicate deliveries
            var unchangedOriginal = await context.TelegramMessageLogs
                .AsNoTracking()
                .SingleAsync(x => x.TelegramMessageLogId == validDbPayload.TelegramMessageLogId);
            Assert.Equal(fixedReceivedUtc, unchangedOriginal.ReceivedAtUtc);
            Assert.Equal("Pending", unchangedOriginal.ParsedStatusText);

            // 10. Confirm zero RFFO status or history changes occurred in Task 5.2
            Assert.Equal(0, await context.ActivityStatuses.CountAsync());
            Assert.Equal(0, await context.ActivityHistories.CountAsync());

            await tx.RollbackAsync();
        });
    }

    [Fact]
    public async Task DatabaseFailure_RetryAfterFailure_AndConcurrentDuplicateRace_ShouldHandleSafely()
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

            var messageBytes = SerializeUpdate(new
            {
                update_id = 200001,
                message = new
                {
                    message_id = 701,
                    date = 1762580000,
                    text = "Activity : RFFO\nBranch Code : 36:01\nStatus : Pending",
                    chat = new { id = ConfiguredSourceChatId, type = "supergroup", title = "Configured Group" },
                    from = new { id = 8001, is_bot = false, first_name = "Anita", username = "anita_3601" }
                }
            });

            // 1. Simulate transient database failure during ingestion -> 503 Service Unavailable (sanitized message)
            var failingIngestion = new SimulatedTransientFailureIngestionService();
            var failingCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(),
                secretHeaderValue: TestSecretToken,
                bodyBytes: messageBytes,
                customIngestionService: failingIngestion);

            var failedResp = await failingCtrl.ReceiveWebhook();
            var failedObj = Assert.IsType<ObjectResult>(failedResp.Result);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, failedObj.StatusCode);
            var failedPayload = Assert.IsType<TelegramWebhookHandlingResult>(failedObj.Value);
            Assert.False(failedPayload.IsSuccess);
            Assert.Equal(TelegramUpdateHandlingOutcome.FailedPersistence, failedPayload.Outcome);
            Assert.DoesNotContain("SqlException", failedPayload.Message, StringComparison.OrdinalIgnoreCase);

            // Verify nothing was persisted during the failed attempt
            Assert.Equal(0, await context.TelegramMessageLogs.CountAsync());

            // 2. Retry after transient failure resolves -> 200 OK (AcceptedForProcessing) and durably persisted
            var retryCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(),
                secretHeaderValue: TestSecretToken,
                bodyBytes: messageBytes);

            var retryResp = await retryCtrl.ReceiveWebhook();
            var retryObj = Assert.IsType<ObjectResult>(retryResp.Result);
            Assert.Equal(StatusCodes.Status200OK, retryObj.StatusCode);
            var retryPayload = Assert.IsType<TelegramWebhookHandlingResult>(retryObj.Value);
            Assert.True(retryPayload.IsSuccess);
            Assert.Equal(TelegramUpdateHandlingOutcome.AcceptedForProcessing, retryPayload.Outcome);
            Assert.Equal(1, await context.TelegramMessageLogs.CountAsync());

            // 3. Database-level concurrent duplicate delivery race condition:
            // Insert a row into context for UpdateId=200002, then invoke SaveLogEntry on a second ingestion call
            // where the row already exists in the database before SaveChangesAsync commits.
            var raceEnvelope = new NormalizedTelegramMessageEnvelope(
                UpdateId: 200002,
                TelegramChatId: ConfiguredSourceChatId,
                ChatType: "supergroup",
                ChatTitle: "Configured Group",
                TelegramMessageId: 702,
                TelegramUserId: 8001,
                IsSenderBot: false,
                SenderUsername: "anita_3601",
                SenderDisplayName: "Anita",
                MessageTimestampUtc: DateTimeOffset.FromUnixTimeSeconds(1762580000).UtcDateTime,
                RawMessageText: "Activity : RFFO\nBranch Code : 36:01\nStatus : Completed");

            var groupAuth = new TelegramGroupAuthorizationResult(
                IsAuthorized: true,
                TelegramChatId: ConfiguredSourceChatId,
                GroupRole: TelegramGroupRole.Source,
                AuthorizationSource: "Configuration",
                AuthorizedActivityCodes: ["RFFO"],
                RejectionReason: null);

            var optionsMonitor = new StaticOptionsMonitor<TelegramBotOptions>(CreateDefaultOptions());
            var ingestionService = new TelegramMessageIngestionService(
                context,
                new TelegramActivityMessageParser(),
                new SystemBusinessClock(),
                optionsMonitor,
                NullLogger<TelegramMessageIngestionService>.Instance);

            var firstIngest = await ingestionService.IngestAuthorizedMessageAsync(raceEnvelope, groupAuth);
            Assert.True(firstIngest.IsPersistedOrDuplicate);
            Assert.False(firstIngest.IsDuplicate);

            var secondIngest = await ingestionService.IngestAuthorizedMessageAsync(raceEnvelope, groupAuth);
            Assert.True(secondIngest.IsPersistedOrDuplicate);
            Assert.True(secondIngest.IsDuplicate);
            Assert.Equal(firstIngest.TelegramMessageLogId, secondIngest.TelegramMessageLogId);

            Assert.Equal(
                1,
                await context.TelegramMessageLogs.CountAsync(x => x.TelegramUpdateId == 200002));

            // 4. Verify SQL Server unique indexes directly enforce UX_TelegramMessageLog_TelegramUpdateId and UX_TelegramMessageLog_ChatId_MessageId
            await context.Database.CurrentTransaction!.CreateSavepointAsync("UniqueCheck1");
            context.TelegramMessageLogs.Add(new TelegramMessageLog
            {
                TelegramUpdateId = 200002,
                TelegramChatId = -100111,
                TelegramMessageId = 99999,
                MessageTimestampUtc = DateTime.UtcNow,
                ReceivedAtUtc = DateTime.UtcNow,
                RawMessageText = "Duplicate UpdateId direct insert",
                ProcessingStatus = MessageProcessingStatus.Received
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            await context.Database.CurrentTransaction.RollbackToSavepointAsync("UniqueCheck1");
            context.ChangeTracker.Clear();

            await context.Database.CurrentTransaction.CreateSavepointAsync("UniqueCheck2");
            context.TelegramMessageLogs.Add(new TelegramMessageLog
            {
                TelegramUpdateId = 299999,
                TelegramChatId = ConfiguredSourceChatId,
                TelegramMessageId = 702,
                MessageTimestampUtc = DateTime.UtcNow,
                ReceivedAtUtc = DateTime.UtcNow,
                RawMessageText = "Duplicate ChatId+MessageId direct insert",
                ProcessingStatus = MessageProcessingStatus.Received
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            await context.Database.CurrentTransaction.RollbackToSavepointAsync("UniqueCheck2");
            context.ChangeTracker.Clear();

            await tx.RollbackAsync();
        });
    }

    [Fact]
    public async Task ConfigurationDiagnostics_AndBotHttpClient_ShouldMaskAllSensitiveTokens()
    {
        await using var context = CreateContext();
        var options = CreateDefaultOptions();

        var viewerPrincipal = new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.Name, "ops.viewer"),
                    new Claim(ClaimTypes.Role, SystemRoles.Viewer)
                },
                OpsConnectAuthenticationHandler.SchemeName));

        var controller = CreateWebhookController(
            context,
            options,
            secretHeaderValue: null,
            bodyBytes: Array.Empty<byte>(),
            user: viewerPrincipal);

        var statusResp = controller.GetConfigurationStatus();
        var okStatus = Assert.IsType<OkObjectResult>(statusResp.Result);
        var diagnostics = Assert.IsType<TelegramBotMaskedDiagnosticsDto>(okStatus.Value);

        Assert.True(diagnostics.IsEnabled);
        Assert.True(diagnostics.IsConfigurationValidForEnabledBot);
        Assert.Equal(TelegramSecretMasker.MaskedPlaceholder, diagnostics.MaskedBotToken);
        Assert.Equal(TelegramSecretMasker.MaskedPlaceholder, diagnostics.MaskedWebhookSecretToken);
        Assert.DoesNotContain(TestBotToken, JsonSerializer.Serialize(diagnostics), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestSecretToken, JsonSerializer.Serialize(diagnostics), StringComparison.OrdinalIgnoreCase);

        using var httpClient = new HttpClient(new FailingTokenEchoHttpMessageHandler());
        var botClient = new TelegramBotHttpClient(
            httpClient,
            new StaticOptionsMonitor<TelegramBotOptions>(options),
            NullLogger<TelegramBotHttpClient>.Instance);

        var sendResult = await botClient.SendMessageAsync(ConfiguredSourceChatId, "Test message");
        Assert.False(sendResult.IsSuccess);
        Assert.NotNull(sendResult.ErrorMessage);
        Assert.DoesNotContain(TestBotToken, sendResult.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"bot{TelegramSecretMasker.MaskedPlaceholder}", sendResult.ErrorMessage!, StringComparison.Ordinal);
    }

    private sealed class SimulatedTransientFailureIngestionService : ITelegramMessageIngestionService
    {
        public Task<TelegramMessageIngestionResult> IngestAuthorizedMessageAsync(
            NormalizedTelegramMessageEnvelope envelope,
            TelegramGroupAuthorizationResult groupAuthorization,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TelegramMessageIngestionResult(
                IsPersistedOrDuplicate: false,
                IsDuplicate: false,
                TelegramMessageLogId: null,
                ProcessingStatus: null,
                ReceivedAtUtc: null,
                ErrorCode: "DatabasePersistenceFailure",
                ErrorMessage: "Temporary service error while persisting Telegram update; please retry."));

        public Task<TelegramMessageIngestionResult> RecordRejectedGroupMessageAsync(
            NormalizedTelegramMessageEnvelope envelope,
            TelegramGroupAuthorizationResult groupAuthorization,
            CancellationToken cancellationToken = default) =>
            IngestAuthorizedMessageAsync(envelope, groupAuthorization, cancellationToken);
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

    private sealed class FailingTokenEchoHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new HttpRequestException($"Connection failed for endpoint {request.RequestUri}");
        }
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T currentValue) => CurrentValue = currentValue;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
