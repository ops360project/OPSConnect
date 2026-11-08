using System.Net;
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
            AuthorizedSourceChatIds = [ConfiguredSourceChatId],
            DestinationChatIds = [-100555000999]
        };

    private static TelegramWebhookController CreateWebhookController(
        OpsConnectDbContext dbContext,
        TelegramBotOptions options,
        string? secretHeaderValue,
        byte[] bodyBytes,
        ClaimsPrincipal? user = null)
    {
        var optionsMonitor = new StaticOptionsMonitor<TelegramBotOptions>(options);
        var normalizer = new TelegramUpdateNormalizer();
        var groupAuthService = new TelegramGroupAuthorizationService(dbContext, optionsMonitor);
        var receiver = new TelegramWebhookReceiver(optionsMonitor, normalizer, groupAuthService);

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
    public async Task WebhookEndpoint_ShouldEnforceSecretValidation_PayloadBounds_UpdateStructure_AndSourceGroupAuthorization()
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

            var rffoActivity = await context.Activities.SingleAsync(a => a.ActivityCode == "RFFO");

            // Seed a database-backed Source TelegramGroup for RFFO
            context.TelegramGroups.Add(new TelegramGroup
            {
                ActivityId = rffoActivity.ActivityId,
                TelegramChatId = DbSourceChatId,
                GroupTitle = "DB Authorised RFFO Source Group",
                GroupRole = TelegramGroupRole.Source,
                IsActive = true,
                CreatedBy = "test.admin"
            });
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
                        title = "DB Authorised RFFO Source Group"
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

            // 1. Missing webhook secret -> 401 Unauthorized
            var missingSecretCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(),
                secretHeaderValue: null,
                bodyBytes: validGroupMessageBytes);

            var missingSecretResp = await missingSecretCtrl.ReceiveWebhook();
            var missingObj = Assert.IsType<ObjectResult>(missingSecretResp.Result);
            Assert.Equal(StatusCodes.Status401Unauthorized, missingObj.StatusCode);
            var missingPayload = Assert.IsType<TelegramWebhookHandlingResult>(missingObj.Value);
            Assert.Equal(TelegramUpdateHandlingOutcome.RejectedUnauthorizedSecret, missingPayload.Outcome);

            // 2. Invalid webhook secret -> 401 Unauthorized
            var invalidSecretCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(),
                secretHeaderValue: "Wrong_Secret_Token_Value_0000",
                bodyBytes: validGroupMessageBytes);

            var invalidSecretResp = await invalidSecretCtrl.ReceiveWebhook();
            var invalidObj = Assert.IsType<ObjectResult>(invalidSecretResp.Result);
            Assert.Equal(StatusCodes.Status401Unauthorized, invalidObj.StatusCode);
            var invalidPayload = Assert.IsType<TelegramWebhookHandlingResult>(invalidObj.Value);
            Assert.Equal(TelegramUpdateHandlingOutcome.RejectedUnauthorizedSecret, invalidPayload.Outcome);

            // 3. Oversized payload -> 413 Payload Too Large
            var oversizedBytes = Encoding.UTF8.GetBytes("{\"update_id\":1,\"padding\":\"" + new string('A', 2048) + "\"}");
            var oversizedCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(maxPayloadBytes: 1024),
                secretHeaderValue: TestSecretToken,
                bodyBytes: oversizedBytes);

            var oversizedResp = await oversizedCtrl.ReceiveWebhook();
            var oversizedObj = Assert.IsType<ObjectResult>(oversizedResp.Result);
            Assert.Equal(StatusCodes.Status413PayloadTooLarge, oversizedObj.StatusCode);
            var oversizedPayload = Assert.IsType<TelegramWebhookHandlingResult>(oversizedObj.Value);
            Assert.Equal(TelegramUpdateHandlingOutcome.RejectedOversizedPayload, oversizedPayload.Outcome);

            // 4. Malformed JSON payload -> 400 Bad Request
            var malformedCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(),
                secretHeaderValue: TestSecretToken,
                bodyBytes: Encoding.UTF8.GetBytes("{not-valid-json"));

            var malformedResp = await malformedCtrl.ReceiveWebhook();
            var malformedObj = Assert.IsType<ObjectResult>(malformedResp.Result);
            Assert.Equal(StatusCodes.Status400BadRequest, malformedObj.StatusCode);
            var malformedPayload = Assert.IsType<TelegramWebhookHandlingResult>(malformedObj.Value);
            Assert.Equal(TelegramUpdateHandlingOutcome.RejectedMalformedPayload, malformedPayload.Outcome);

            // 5. Unsupported update type (callback_query) -> 200 OK (IgnoredNonMessageUpdate)
            var callbackBytes = SerializeUpdate(new
            {
                update_id = 100002,
                callback_query = new { id = "cb-99" }
            });
            var callbackCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(),
                secretHeaderValue: TestSecretToken,
                bodyBytes: callbackBytes);

            var callbackResp = await callbackCtrl.ReceiveWebhook();
            var callbackObj = Assert.IsType<ObjectResult>(callbackResp.Result);
            Assert.Equal(StatusCodes.Status200OK, callbackObj.StatusCode);
            var callbackPayload = Assert.IsType<TelegramWebhookHandlingResult>(callbackObj.Value);
            Assert.Equal(TelegramUpdateHandlingOutcome.IgnoredNonMessageUpdate, callbackPayload.Outcome);

            // 6. Unauthorised source group Chat ID -> 403 Forbidden (RejectedUnauthorizedSourceGroup)
            var unauthorizedGroupBytes = SerializeUpdate(new
            {
                update_id = 100003,
                message = new
                {
                    message_id = 502,
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
                bodyBytes: unauthorizedGroupBytes);

            var unauthorizedGroupResp = await unauthorizedGroupCtrl.ReceiveWebhook();
            var unauthorizedGroupObj = Assert.IsType<ObjectResult>(unauthorizedGroupResp.Result);
            Assert.Equal(StatusCodes.Status403Forbidden, unauthorizedGroupObj.StatusCode);
            var unauthorizedGroupPayload = Assert.IsType<TelegramWebhookHandlingResult>(unauthorizedGroupObj.Value);
            Assert.Equal(TelegramUpdateHandlingOutcome.RejectedUnauthorizedSourceGroup, unauthorizedGroupPayload.Outcome);

            // 7. Valid webhook secret + Authorised DB Source Group -> 200 OK (AcceptedForProcessing)
            // Verify Open Group Mode metadata is captured and NO ActivityStatus/ActivityHistory rows are written in Task 5.1
            var validDbGroupCtrl = CreateWebhookController(
                context,
                CreateDefaultOptions(),
                secretHeaderValue: TestSecretToken,
                bodyBytes: validGroupMessageBytes);

            var validDbResp = await validDbGroupCtrl.ReceiveWebhook();
            var validDbObj = Assert.IsType<ObjectResult>(validDbResp.Result);
            Assert.Equal(StatusCodes.Status200OK, validDbObj.StatusCode);
            var validDbPayload = Assert.IsType<TelegramWebhookHandlingResult>(validDbObj.Value);
            Assert.True(validDbPayload.IsSuccess);
            Assert.Equal(TelegramUpdateHandlingOutcome.AcceptedForProcessing, validDbPayload.Outcome);
            Assert.NotNull(validDbPayload.NormalizedEnvelope);
            Assert.Equal(DbSourceChatId, validDbPayload.NormalizedEnvelope!.TelegramChatId);
            Assert.Equal(900123, validDbPayload.NormalizedEnvelope.TelegramUserId);
            Assert.Equal("suresh_3601", validDbPayload.NormalizedEnvelope.SenderUsername);
            Assert.Equal("Suresh Nayak", validDbPayload.NormalizedEnvelope.SenderDisplayName);
            Assert.Contains("RFFO", validDbPayload.AuthorizedActivityCodes!);

            Assert.Equal(0, await context.ActivityStatuses.CountAsync());
            Assert.Equal(0, await context.ActivityHistories.CountAsync());

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

        // Verify TelegramBotHttpClient masks BotToken when HTTP failure occurs
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
