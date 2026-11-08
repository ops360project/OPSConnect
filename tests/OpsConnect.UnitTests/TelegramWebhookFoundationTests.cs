using OpsConnect.Application.Telegram;
using OpsConnect.Domain.Telegram;

namespace OpsConnect.UnitTests;

public class TelegramWebhookFoundationTests
{
    private const string ValidSecret = "OpsConnect_Webhook_Secret_2026_Valid99";
    private const string ValidBotToken = "123456789:AAH_ValidBotTokenSecretString_12345";

    [Fact]
    public void VerifyWebhookSecretToken_WithMatchingSecret_ShouldReturnTrue()
    {
        bool isValid = TelegramBotOptionsValidator.VerifyWebhookSecretToken(ValidSecret, ValidSecret);
        Assert.True(isValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Wrong_Secret_Value_1234567890")]
    [InlineData("OpsConnect_Webhook_Secret_2026_Valid9")]
    [InlineData("opsconnect_webhook_secret_2026_valid99")]
    public void VerifyWebhookSecretToken_WithMissingOrInvalidProvidedSecret_ShouldReturnFalse(string? providedSecret)
    {
        bool isValid = TelegramBotOptionsValidator.VerifyWebhookSecretToken(providedSecret, ValidSecret);
        Assert.False(isValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<SET_VIA_USER_SECRETS_OR_ENV>")]
    [InlineData("YOUR_WEBHOOK_SECRET")]
    public void VerifyWebhookSecretToken_WhenConfiguredSecretIsPlaceholder_ShouldReturnFalse(string? configuredSecret)
    {
        bool isValid = TelegramBotOptionsValidator.VerifyWebhookSecretToken(ValidSecret, configuredSecret);
        Assert.False(isValid);
    }

    [Fact]
    public void ValidateConfiguration_WhenDisabledWithPlaceholders_ShouldSucceed()
    {
        var options = new TelegramBotOptions
        {
            IsEnabled = false,
            BotToken = "<SET_VIA_USER_SECRETS_OR_ENV>",
            WebhookUrl = "https://example.opsconnect.local/api/telegram/webhook",
            WebhookSecretToken = "<SET_VIA_USER_SECRETS_OR_ENV>"
        };

        var result = TelegramBotOptionsValidator.ValidateConfiguration(options);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidateConfiguration_WhenEnabledWithValidSettings_ShouldSucceed()
    {
        var options = new TelegramBotOptions
        {
            IsEnabled = true,
            BotToken = ValidBotToken,
            WebhookUrl = "https://opsconnect.example.com/api/telegram/webhook",
            WebhookSecretToken = ValidSecret,
            MaxWebhookPayloadBytes = 65536,
            AuthorizedSourceChatIds = [-100111222333],
            DestinationChatIds = [-100444555666]
        };

        var result = TelegramBotOptionsValidator.ValidateConfiguration(options);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidateConfiguration_WhenEnabledWithInvalidSettings_ShouldReportAllValidationErrors()
    {
        var options = new TelegramBotOptions
        {
            IsEnabled = true,
            BotToken = "invalid-token",
            WebhookUrl = "http://insecure.example.com/webhook",
            WebhookSecretToken = "short",
            MaxWebhookPayloadBytes = 500,
            AuthorizedSourceChatIds = [0],
            DestinationChatIds = [0]
        };

        var result = TelegramBotOptionsValidator.ValidateConfiguration(options);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("BotToken", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, e => e.Contains("HTTPS", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, e => e.Contains("WebhookSecretToken", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, e => e.Contains("MaxWebhookPayloadBytes", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, e => e.Contains("AuthorizedSourceChatIds", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, e => e.Contains("DestinationChatIds", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SecretMasker_ShouldMaskSecretsInDiagnosticsAndSanitizeLogMessages()
    {
        var options = new TelegramBotOptions
        {
            IsEnabled = true,
            BotToken = ValidBotToken,
            WebhookUrl = "https://opsconnect.example.com/api/telegram/webhook",
            WebhookSecretToken = ValidSecret,
            AuthorizedSourceChatIds = [-100123456]
        };

        var validation = TelegramBotOptionsValidator.ValidateConfiguration(options);
        var diagnostics = options.ToMaskedDiagnostics(validation);

        Assert.True(diagnostics.HasBotToken);
        Assert.Equal(TelegramSecretMasker.MaskedPlaceholder, diagnostics.MaskedBotToken);
        Assert.True(diagnostics.HasWebhookSecretToken);
        Assert.Equal(TelegramSecretMasker.MaskedPlaceholder, diagnostics.MaskedWebhookSecretToken);

        var rawError =
            $"Failed request to https://api.telegram.org/bot{ValidBotToken}/sendMessage with secret {ValidSecret}";
        var sanitized = TelegramSecretMasker.SanitizeForLogging(rawError, ValidBotToken, ValidSecret);

        Assert.DoesNotContain(ValidBotToken, sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ValidSecret, sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"bot{TelegramSecretMasker.MaskedPlaceholder}", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_WithValidGroupTextMessage_ShouldExtractOpenGroupModeSenderMetadata()
    {
        var normalizer = new TelegramUpdateNormalizer();
        var update = new TelegramWebhookUpdatePayload
        {
            UpdateId = 90001,
            Message = new TelegramWebhookMessagePayload
            {
                MessageId = 450,
                DateUnixSeconds = 1762580000,
                Text = "  Activity : RFFO\nBranch Code : 36:01\nStatus : Pending  ",
                Chat = new TelegramWebhookChatPayload
                {
                    Id = -100987654321,
                    Type = "supergroup",
                    Title = "Odisha RFFO Source Group"
                },
                From = new TelegramWebhookUserPayload
                {
                    Id = 778899,
                    IsBot = false,
                    FirstName = "Ramesh",
                    LastName = "dash",
                    Username = "ramesh_3601"
                }
            }
        };

        var result = normalizer.Normalize(update);

        Assert.True(result.IsValid);
        Assert.True(result.IsTextMessageCandidate);
        Assert.NotNull(result.Envelope);
        Assert.Equal(90001, result.Envelope!.UpdateId);
        Assert.Equal(-100987654321, result.Envelope.TelegramChatId);
        Assert.Equal("supergroup", result.Envelope.ChatType);
        Assert.Equal(450, result.Envelope.TelegramMessageId);
        Assert.Equal(778899, result.Envelope.TelegramUserId);
        Assert.Equal("ramesh_3601", result.Envelope.SenderUsername);
        Assert.Equal("Ramesh dash", result.Envelope.SenderDisplayName);
        Assert.Equal("Activity : RFFO\nBranch Code : 36:01\nStatus : Pending", result.Envelope.RawMessageText);
    }

    [Fact]
    public void Normalize_WithNonMessageUpdates_ShouldClassifySafelyWithoutError()
    {
        var normalizer = new TelegramUpdateNormalizer();

        // 1. Edited message
        var editedUpdate = new TelegramWebhookUpdatePayload
        {
            UpdateId = 90002,
            EditedMessage = new TelegramWebhookMessagePayload { MessageId = 10, Text = "Edited" }
        };
        var editedResult = normalizer.Normalize(editedUpdate);
        Assert.True(editedResult.IsValid);
        Assert.False(editedResult.IsTextMessageCandidate);
        Assert.Equal("edited_message", editedResult.UpdateCategory);

        // 2. Callback query
        var callbackUpdate = new TelegramWebhookUpdatePayload
        {
            UpdateId = 90003,
            CallbackQuery = new { id = "cb1" }
        };
        var callbackResult = normalizer.Normalize(callbackUpdate);
        Assert.True(callbackResult.IsValid);
        Assert.False(callbackResult.IsTextMessageCandidate);
        Assert.Equal("callback_query", callbackResult.UpdateCategory);

        // 3. Bot-originated message
        var botMessageUpdate = new TelegramWebhookUpdatePayload
        {
            UpdateId = 90004,
            Message = new TelegramWebhookMessagePayload
            {
                MessageId = 11,
                DateUnixSeconds = 1762580000,
                Text = "Bot reply",
                Chat = new TelegramWebhookChatPayload { Id = -100123, Type = "group" },
                From = new TelegramWebhookUserPayload { Id = 111, IsBot = true, FirstName = "OpsConnectBot" }
            }
        };
        var botResult = normalizer.Normalize(botMessageUpdate);
        Assert.True(botResult.IsValid);
        Assert.False(botResult.IsTextMessageCandidate);
        Assert.Equal("bot_message", botResult.UpdateCategory);

        // 4. Non-text media/service message
        var photoMessageUpdate = new TelegramWebhookUpdatePayload
        {
            UpdateId = 90005,
            Message = new TelegramWebhookMessagePayload
            {
                MessageId = 12,
                DateUnixSeconds = 1762580000,
                Text = null,
                Caption = "Photo caption",
                Chat = new TelegramWebhookChatPayload { Id = -100123, Type = "group" },
                From = new TelegramWebhookUserPayload { Id = 222, IsBot = false, FirstName = "Operator" }
            }
        };
        var photoResult = normalizer.Normalize(photoMessageUpdate);
        Assert.True(photoResult.IsValid);
        Assert.False(photoResult.IsTextMessageCandidate);
        Assert.Equal("non_text_message", photoResult.UpdateCategory);
    }

    [Fact]
    public void Normalize_WithMalformedMessageStructure_ShouldReturnInvalidPayload()
    {
        var normalizer = new TelegramUpdateNormalizer();

        Assert.False(normalizer.Normalize(null).IsValid);
        Assert.False(normalizer.Normalize(new TelegramWebhookUpdatePayload { UpdateId = 0 }).IsValid);

        var missingChatId = new TelegramWebhookUpdatePayload
        {
            UpdateId = 100,
            Message = new TelegramWebhookMessagePayload
            {
                MessageId = 1,
                DateUnixSeconds = 1762580000,
                Text = "Hello",
                Chat = new TelegramWebhookChatPayload { Id = null }
            }
        };
        Assert.False(normalizer.Normalize(missingChatId).IsValid);
    }

    [Fact]
    public void TelegramMessageLog_LifecycleStateTransitions_ShouldDistinguishTerminalRecoverableAndStaleProcessing()
    {
        var t0 = new DateTime(2026, 11, 8, 8, 0, 0, DateTimeKind.Utc);
        var timeout = TimeSpan.FromSeconds(60);

        var log = new TelegramMessageLog
        {
            TelegramUpdateId = 50001,
            TelegramChatId = -100123456,
            TelegramMessageId = 10,
            MessageTimestampUtc = t0,
            ReceivedAtUtc = t0,
            RawMessageText = "Activity : RFFO\nBranch Code : 36:01\nStatus : Pending",
            ProcessingStatus = MessageProcessingStatus.Received
        };

        // 1. Received is not terminal and can start processing
        Assert.False(log.IsTerminallyProcessed);
        Assert.False(log.IsActivelyProcessing(t0, timeout));
        Assert.True(log.CanStartOrRecoverProcessing(t0, timeout));

        // 2. MarkProcessing transitions to Processing and records ProcessingStartedAtUtc
        log.MarkProcessing(t0);
        Assert.Equal(MessageProcessingStatus.Processing, log.ProcessingStatus);
        Assert.Equal(t0, log.ProcessingStartedAtUtc);
        Assert.False(log.IsTerminallyProcessed);
        Assert.True(log.IsActivelyProcessing(t0.AddSeconds(30), timeout));
        Assert.False(log.CanStartOrRecoverProcessing(t0.AddSeconds(30), timeout));

        // 3. Stale Processing (after timeout) becomes recoverable
        Assert.False(log.IsActivelyProcessing(t0.AddSeconds(61), timeout));
        Assert.True(log.CanStartOrRecoverProcessing(t0.AddSeconds(61), timeout));

        // 4. FailedError is recoverable
        log.MarkProcessingFailed("Transient failure during processing.");
        Assert.Equal(MessageProcessingStatus.FailedError, log.ProcessingStatus);
        Assert.Null(log.ProcessingStartedAtUtc);
        Assert.Null(log.ProcessedAtUtc);
        Assert.False(log.IsTerminallyProcessed);
        Assert.True(log.CanStartOrRecoverProcessing(t0.AddSeconds(63), timeout));

        // 5. MarkCompleted with Accepted or Rejected* is terminal and never recoverable
        log.MarkProcessing(t0.AddSeconds(65));
        log.MarkCompleted(
            finalStatus: MessageProcessingStatus.Accepted,
            processedAtUtc: t0.AddSeconds(66),
            activityId: 1,
            branchId: 10,
            reportingDate: new DateOnly(2026, 11, 8),
            parsedActivityCode: "RFFO",
            parsedBranchCode: "36:01",
            parsedStatusText: "Pending",
            rejectionReason: null);

        Assert.Equal(MessageProcessingStatus.Accepted, log.ProcessingStatus);
        Assert.Null(log.ProcessingStartedAtUtc);
        Assert.Equal(t0.AddSeconds(66), log.ProcessedAtUtc);
        Assert.True(log.IsTerminallyProcessed);
        Assert.False(log.CanStartOrRecoverProcessing(t0.AddMinutes(10), timeout));
    }
}
