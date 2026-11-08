namespace OpsConnect.Application.Telegram;

/// <summary>
/// Normalises and validates incoming Telegram Update structures, distinguishing actionable text messages
/// from non-message updates and extracting Open Group Mode sender audit metadata.
/// </summary>
public interface ITelegramUpdateNormalizer
{
    TelegramUpdateNormalizationResult Normalize(TelegramWebhookUpdatePayload? update);
}

/// <summary>
/// Evaluates whether a Telegram group/supergroup Chat ID is authorised as a Source or Destination group
/// across database-configured TelegramGroups and application configuration.
/// </summary>
public interface ITelegramGroupAuthorizationService
{
    Task<TelegramGroupAuthorizationResult> AuthorizeSourceGroupAsync(
        long telegramChatId,
        CancellationToken cancellationToken = default);

    Task<bool> IsAuthorizedDestinationGroupAsync(
        long telegramChatId,
        string? activityCode = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Abstraction over the outbound Telegram Bot HTTP API for sending replies/reports and managing webhooks.
/// </summary>
public interface ITelegramBotClient
{
    Task<TelegramBotSendResult> SendMessageAsync(
        long chatId,
        string text,
        long? replyToMessageId = null,
        CancellationToken cancellationToken = default);

    Task<TelegramBotSendResult> SetWebhookAsync(
        string webhookUrl,
        string secretToken,
        CancellationToken cancellationToken = default);

    Task<TelegramBotSendResult> DeleteWebhookAsync(
        CancellationToken cancellationToken = default);

    Task<TelegramWebhookInfoResult> GetWebhookInfoAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Persists incoming Telegram messages durably and atomically to telegram.TelegramMessageLog
/// with database-backed idempotency on TelegramUpdateId and (TelegramChatId, TelegramMessageId).
/// </summary>
public interface ITelegramMessageIngestionService
{
    Task<TelegramMessageIngestionResult> IngestAuthorizedMessageAsync(
        NormalizedTelegramMessageEnvelope envelope,
        TelegramGroupAuthorizationResult groupAuthorization,
        CancellationToken cancellationToken = default);

    Task<TelegramMessageIngestionResult> RecordRejectedGroupMessageAsync(
        NormalizedTelegramMessageEnvelope envelope,
        TelegramGroupAuthorizationResult groupAuthorization,
        CancellationToken cancellationToken = default);

    Task<TelegramMessageActivityProcessingResult> ProcessIngestedMessageAsync(
        long telegramMessageLogId,
        NormalizedTelegramMessageEnvelope envelope,
        TelegramGroupAuthorizationResult groupAuthorization,
        CancellationToken cancellationToken = default);

    Task<TelegramAcknowledgementDeliveryResult> DeliverAcknowledgementAsync(
        long telegramMessageLogId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates Telegram webhook security validation, payload size bounds, JSON deserialization,
/// update normalisation, source group authorisation, and durable message ingestion.
/// </summary>
public interface ITelegramWebhookReceiver
{
    Task<TelegramWebhookHandlingResult> ReceiveWebhookAsync(
        string? providedSecretTokenHeader,
        ReadOnlyMemory<byte> rawUtf8Payload,
        CancellationToken cancellationToken = default);

    TelegramBotMaskedDiagnosticsDto GetMaskedConfigurationDiagnostics();
}

/// <summary>
/// Operational health checks, integration status queries, and diagnostic reporting for Telegram bot infrastructure.
/// </summary>
public interface ITelegramOperationalMonitoringService
{
    Task<TelegramHealthReportDto> CheckHealthAsync(
        bool verifyExternalTelegramApi = false,
        CancellationToken cancellationToken = default);

    Task<TelegramOperationalStatusDto> GetOperationalStatusAsync(
        bool verifyExternalTelegramApi = false,
        CancellationToken cancellationToken = default);

    Task<TelegramOperationalDiagnosticsDto> GetOperationalDiagnosticsAsync(
        TelegramOperationalDiagnosticsQuery query,
        CancellationToken cancellationToken = default);
}
