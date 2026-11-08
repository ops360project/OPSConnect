using System.Text.Json;
using Microsoft.Extensions.Options;
using OpsConnect.Domain.Telegram;

namespace OpsConnect.Application.Telegram;

/// <summary>
/// Coordinates Telegram webhook security verification, payload size enforcement, JSON deserialization,
/// update normalisation, Open Group Mode source-group authorisation, durable message ingestion,
/// and synchronous activity status processing via ITelegramMessageIngestionService.
/// </summary>
public sealed class TelegramWebhookReceiver : ITelegramWebhookReceiver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32
    };

    private readonly IOptionsMonitor<TelegramBotOptions> _optionsMonitor;
    private readonly ITelegramUpdateNormalizer _updateNormalizer;
    private readonly ITelegramGroupAuthorizationService _groupAuthorizationService;
    private readonly ITelegramMessageIngestionService _messageIngestionService;

    public TelegramWebhookReceiver(
        IOptionsMonitor<TelegramBotOptions> optionsMonitor,
        ITelegramUpdateNormalizer updateNormalizer,
        ITelegramGroupAuthorizationService groupAuthorizationService,
        ITelegramMessageIngestionService messageIngestionService)
    {
        ArgumentNullException.ThrowIfNull(optionsMonitor);
        ArgumentNullException.ThrowIfNull(updateNormalizer);
        ArgumentNullException.ThrowIfNull(groupAuthorizationService);
        ArgumentNullException.ThrowIfNull(messageIngestionService);

        _optionsMonitor = optionsMonitor;
        _updateNormalizer = updateNormalizer;
        _groupAuthorizationService = groupAuthorizationService;
        _messageIngestionService = messageIngestionService;
    }

    public async Task<TelegramWebhookHandlingResult> ReceiveWebhookAsync(
        string? providedSecretTokenHeader,
        ReadOnlyMemory<byte> rawUtf8Payload,
        CancellationToken cancellationToken = default)
    {
        var options = _optionsMonitor.CurrentValue;

        // 1. Constant-time webhook secret token verification
        if (!TelegramBotOptionsValidator.VerifyWebhookSecretToken(providedSecretTokenHeader, options.WebhookSecretToken))
        {
            return new TelegramWebhookHandlingResult(
                IsSuccess: false,
                Outcome: TelegramUpdateHandlingOutcome.RejectedUnauthorizedSecret,
                RecommendedHttpStatusCode: 401,
                UpdateId: null,
                TelegramChatId: null,
                TelegramMessageId: null,
                ErrorCode: "UnauthorizedWebhookSecret",
                Message: string.IsNullOrWhiteSpace(providedSecretTokenHeader)
                    ? "Missing Telegram webhook secret token header."
                    : "Invalid Telegram webhook secret token.");
        }

        // 2. Payload size bounds check
        int maxBytes = options.MaxWebhookPayloadBytes > 0
            ? options.MaxWebhookPayloadBytes
            : TelegramBotOptions.DefaultMaxWebhookPayloadBytes;

        if (rawUtf8Payload.Length > maxBytes)
        {
            return new TelegramWebhookHandlingResult(
                IsSuccess: false,
                Outcome: TelegramUpdateHandlingOutcome.RejectedOversizedPayload,
                RecommendedHttpStatusCode: 413,
                UpdateId: null,
                TelegramChatId: null,
                TelegramMessageId: null,
                ErrorCode: "OversizedWebhookPayload",
                Message: $"Telegram webhook payload exceeds maximum allowed size of {maxBytes} bytes.");
        }

        if (rawUtf8Payload.IsEmpty || IsAllWhitespace(rawUtf8Payload.Span))
        {
            return new TelegramWebhookHandlingResult(
                IsSuccess: false,
                Outcome: TelegramUpdateHandlingOutcome.RejectedMalformedPayload,
                RecommendedHttpStatusCode: 400,
                UpdateId: null,
                TelegramChatId: null,
                TelegramMessageId: null,
                ErrorCode: "EmptyWebhookPayload",
                Message: "Telegram webhook request body cannot be empty.");
        }

        // 3. JSON deserialization
        TelegramWebhookUpdatePayload? updatePayload;
        try
        {
            updatePayload = JsonSerializer.Deserialize<TelegramWebhookUpdatePayload>(rawUtf8Payload.Span, JsonOptions);
        }
        catch (JsonException)
        {
            return new TelegramWebhookHandlingResult(
                IsSuccess: false,
                Outcome: TelegramUpdateHandlingOutcome.RejectedMalformedPayload,
                RecommendedHttpStatusCode: 400,
                UpdateId: null,
                TelegramChatId: null,
                TelegramMessageId: null,
                ErrorCode: "MalformedWebhookJson",
                Message: "Telegram webhook payload is not valid JSON.");
        }

        // 4. Bot enabled check
        if (!options.IsEnabled)
        {
            return new TelegramWebhookHandlingResult(
                IsSuccess: true,
                Outcome: TelegramUpdateHandlingOutcome.IgnoredBotDisabled,
                RecommendedHttpStatusCode: 200,
                UpdateId: updatePayload?.UpdateId,
                TelegramChatId: updatePayload?.Message?.Chat?.Id,
                TelegramMessageId: updatePayload?.Message?.MessageId,
                ErrorCode: null,
                Message: "Telegram bot processing is currently disabled.");
        }

        // 5. Structural validation and update normalisation
        var normalization = _updateNormalizer.Normalize(updatePayload);
        if (!normalization.IsValid)
        {
            return new TelegramWebhookHandlingResult(
                IsSuccess: false,
                Outcome: TelegramUpdateHandlingOutcome.RejectedMalformedPayload,
                RecommendedHttpStatusCode: 400,
                UpdateId: updatePayload?.UpdateId,
                TelegramChatId: updatePayload?.Message?.Chat?.Id,
                TelegramMessageId: updatePayload?.Message?.MessageId,
                ErrorCode: "InvalidTelegramUpdateStructure",
                Message: normalization.ErrorReason ?? "Telegram update structure is invalid.");
        }

        if (!normalization.IsTextMessageCandidate || normalization.Envelope is null)
        {
            return new TelegramWebhookHandlingResult(
                IsSuccess: true,
                Outcome: TelegramUpdateHandlingOutcome.IgnoredNonMessageUpdate,
                RecommendedHttpStatusCode: 200,
                UpdateId: updatePayload?.UpdateId,
                TelegramChatId: updatePayload?.Message?.Chat?.Id,
                TelegramMessageId: updatePayload?.Message?.MessageId,
                ErrorCode: null,
                Message: normalization.ErrorReason ?? $"Non-message update '{normalization.UpdateCategory}' safely ignored.");
        }

        var envelope = normalization.Envelope;

        // 6. Open Group Mode source-group authorisation (Database-First Precedence over static configuration)
        TelegramGroupAuthorizationResult groupAuth;
        try
        {
            groupAuth = await _groupAuthorizationService.AuthorizeSourceGroupAsync(
                envelope.TelegramChatId,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TelegramWebhookHandlingResult(
                IsSuccess: false,
                Outcome: TelegramUpdateHandlingOutcome.FailedPersistence,
                RecommendedHttpStatusCode: 503,
                UpdateId: envelope.UpdateId,
                TelegramChatId: envelope.TelegramChatId,
                TelegramMessageId: envelope.TelegramMessageId,
                ErrorCode: "WebhookPersistenceUnavailable",
                Message: "Temporary service error while validating Telegram source group; please retry.");
        }

        if (!groupAuth.IsAuthorized)
        {
            var rejectedIngestion = await _messageIngestionService.RecordRejectedGroupMessageAsync(
                envelope,
                groupAuth,
                cancellationToken);

            if (!rejectedIngestion.IsPersistedOrDuplicate)
            {
                return new TelegramWebhookHandlingResult(
                    IsSuccess: false,
                    Outcome: TelegramUpdateHandlingOutcome.FailedPersistence,
                    RecommendedHttpStatusCode: 503,
                    UpdateId: envelope.UpdateId,
                    TelegramChatId: envelope.TelegramChatId,
                    TelegramMessageId: envelope.TelegramMessageId,
                    ErrorCode: rejectedIngestion.ErrorCode ?? "WebhookPersistenceUnavailable",
                    Message: rejectedIngestion.ErrorMessage ?? "Temporary service error while persisting Telegram update; please retry.");
            }

            if (rejectedIngestion.IsDuplicate)
            {
                return new TelegramWebhookHandlingResult(
                    IsSuccess: true,
                    Outcome: TelegramUpdateHandlingOutcome.DuplicateIgnored,
                    RecommendedHttpStatusCode: 200,
                    UpdateId: envelope.UpdateId,
                    TelegramChatId: envelope.TelegramChatId,
                    TelegramMessageId: envelope.TelegramMessageId,
                    ErrorCode: null,
                    Message: "Duplicate Telegram webhook delivery recognised and safely ignored.",
                    NormalizedEnvelope: envelope,
                    TelegramMessageLogId: rejectedIngestion.TelegramMessageLogId,
                    PersistedProcessingStatus: rejectedIngestion.ProcessingStatus,
                    ReceivedAtUtc: rejectedIngestion.ReceivedAtUtc,
                    AcknowledgementStatus: AcknowledgementDeliveryStatus.NotApplicable);
            }

            return new TelegramWebhookHandlingResult(
                IsSuccess: false,
                Outcome: TelegramUpdateHandlingOutcome.RejectedUnauthorizedSourceGroup,
                RecommendedHttpStatusCode: 403,
                UpdateId: envelope.UpdateId,
                TelegramChatId: envelope.TelegramChatId,
                TelegramMessageId: envelope.TelegramMessageId,
                ErrorCode: "UnauthorizedSourceGroup",
                Message: groupAuth.RejectionReason ?? $"Telegram chat '{envelope.TelegramChatId}' is not an authorised source group.",
                NormalizedEnvelope: envelope,
                TelegramMessageLogId: rejectedIngestion.TelegramMessageLogId,
                PersistedProcessingStatus: rejectedIngestion.ProcessingStatus,
                ReceivedAtUtc: rejectedIngestion.ReceivedAtUtc,
                AcknowledgementStatus: AcknowledgementDeliveryStatus.NotApplicable);
        }

        // 7. Durable atomic ingestion into telegram.TelegramMessageLog
        var ingestion = await _messageIngestionService.IngestAuthorizedMessageAsync(
            envelope,
            groupAuth,
            cancellationToken);

        if (!ingestion.IsPersistedOrDuplicate || !ingestion.TelegramMessageLogId.HasValue)
        {
            return new TelegramWebhookHandlingResult(
                IsSuccess: false,
                Outcome: TelegramUpdateHandlingOutcome.FailedPersistence,
                RecommendedHttpStatusCode: 503,
                UpdateId: envelope.UpdateId,
                TelegramChatId: envelope.TelegramChatId,
                TelegramMessageId: envelope.TelegramMessageId,
                ErrorCode: ingestion.ErrorCode ?? "WebhookPersistenceUnavailable",
                Message: ingestion.ErrorMessage ?? "Temporary service error while persisting Telegram update; please retry.");
        }

        if (ingestion.IsDuplicate && !ingestion.RequiresActivityProcessing && !ingestion.RequiresAcknowledgementDelivery)
        {
            return new TelegramWebhookHandlingResult(
                IsSuccess: true,
                Outcome: TelegramUpdateHandlingOutcome.DuplicateIgnored,
                RecommendedHttpStatusCode: 200,
                UpdateId: envelope.UpdateId,
                TelegramChatId: envelope.TelegramChatId,
                TelegramMessageId: envelope.TelegramMessageId,
                ErrorCode: null,
                Message: "Duplicate Telegram webhook delivery recognised and safely ignored.",
                NormalizedEnvelope: envelope,
                AuthorizedActivityCodes: groupAuth.AuthorizedActivityCodes,
                TelegramMessageLogId: ingestion.TelegramMessageLogId,
                PersistedProcessingStatus: ingestion.ProcessingStatus,
                ReceivedAtUtc: ingestion.ReceivedAtUtc);
        }

        MessageProcessingStatus finalProcessingStatus = ingestion.ProcessingStatus ?? MessageProcessingStatus.Received;
        OpsConnect.Application.Activities.Common.ActivityUpdateProcessingResult? activityResult = null;

        // 8. Process the newly ingested (or retry-recoverable) message through IActivityUpdateProcessor
        //    Note: Skipped when RequiresActivityProcessing == false and RequiresAcknowledgementDelivery == true,
        //    ensuring an acknowledgement retry NEVER executes RFFO activity processing a second time!
        if (ingestion.RequiresActivityProcessing)
        {
            var processing = await _messageIngestionService.ProcessIngestedMessageAsync(
                ingestion.TelegramMessageLogId.Value,
                envelope,
                groupAuth,
                cancellationToken);

            if (!processing.IsCompleted)
            {
                return new TelegramWebhookHandlingResult(
                    IsSuccess: false,
                    Outcome: TelegramUpdateHandlingOutcome.ProcessingFailed,
                    RecommendedHttpStatusCode: 503,
                    UpdateId: envelope.UpdateId,
                    TelegramChatId: envelope.TelegramChatId,
                    TelegramMessageId: envelope.TelegramMessageId,
                    ErrorCode: "ActivityProcessingFailed",
                    Message: processing.FailureReason ?? "Temporary service error while processing activity update; please retry.",
                    NormalizedEnvelope: envelope,
                    AuthorizedActivityCodes: groupAuth.AuthorizedActivityCodes,
                    TelegramMessageLogId: ingestion.TelegramMessageLogId,
                    PersistedProcessingStatus: processing.FinalProcessingStatus,
                    ReceivedAtUtc: ingestion.ReceivedAtUtc,
                    AcknowledgementStatus: AcknowledgementDeliveryStatus.NotApplicable);
            }

            if (processing.IsDuplicateOrAlreadyProcessed)
            {
                return new TelegramWebhookHandlingResult(
                    IsSuccess: true,
                    Outcome: TelegramUpdateHandlingOutcome.DuplicateIgnored,
                    RecommendedHttpStatusCode: 200,
                    UpdateId: envelope.UpdateId,
                    TelegramChatId: envelope.TelegramChatId,
                    TelegramMessageId: envelope.TelegramMessageId,
                    ErrorCode: null,
                    Message: "Duplicate or concurrently processed Telegram update recognised and safely ignored.",
                    NormalizedEnvelope: envelope,
                    AuthorizedActivityCodes: groupAuth.AuthorizedActivityCodes,
                    TelegramMessageLogId: ingestion.TelegramMessageLogId,
                    PersistedProcessingStatus: processing.FinalProcessingStatus,
                    ReceivedAtUtc: ingestion.ReceivedAtUtc,
                    ActivityProcessingResult: processing.ActivityResult);
            }

            finalProcessingStatus = processing.FinalProcessingStatus;
            activityResult = processing.ActivityResult;
        }

        // 9. Deliver (or recover delivery of) the outbound Telegram group acknowledgement reply
        var ackDelivery = await _messageIngestionService.DeliverAcknowledgementAsync(
            ingestion.TelegramMessageLogId.Value,
            cancellationToken);

        bool isBusinessAccepted =
            finalProcessingStatus is MessageProcessingStatus.Accepted or MessageProcessingStatus.AcceptedUnchanged;

        var finalMessage = ackDelivery.AcknowledgementText
            ?? activityResult?.ReplyMessage
            ?? (isBusinessAccepted
                ? "Telegram activity update processed successfully."
                : "Telegram activity update rejected by business validation.");

        if (!ackDelivery.IsDelivered && ackDelivery.IsRetryableFailure)
        {
            return new TelegramWebhookHandlingResult(
                IsSuccess: false,
                Outcome: TelegramUpdateHandlingOutcome.AcknowledgementDeliveryFailed,
                RecommendedHttpStatusCode: 503,
                UpdateId: envelope.UpdateId,
                TelegramChatId: envelope.TelegramChatId,
                TelegramMessageId: envelope.TelegramMessageId,
                ErrorCode: ackDelivery.ErrorCode ?? "AcknowledgementDeliveryFailed",
                Message: ackDelivery.ErrorMessage ?? "Activity processed, but temporary error occurred while sending Telegram acknowledgement; please retry.",
                NormalizedEnvelope: envelope,
                AuthorizedActivityCodes: groupAuth.AuthorizedActivityCodes,
                TelegramMessageLogId: ingestion.TelegramMessageLogId,
                PersistedProcessingStatus: finalProcessingStatus,
                ReceivedAtUtc: ingestion.ReceivedAtUtc,
                ActivityProcessingResult: activityResult,
                AcknowledgementStatus: ackDelivery.DeliveryStatus,
                ReplyTelegramMessageId: ackDelivery.ReplyTelegramMessageId,
                AcknowledgementDeliveryResult: ackDelivery);
        }

        var finalOutcome = isBusinessAccepted
            ? TelegramUpdateHandlingOutcome.ProcessedSuccessfully
            : TelegramUpdateHandlingOutcome.RejectedActivitySubmission;

        return new TelegramWebhookHandlingResult(
            IsSuccess: true,
            Outcome: finalOutcome,
            RecommendedHttpStatusCode: 200,
            UpdateId: envelope.UpdateId,
            TelegramChatId: envelope.TelegramChatId,
            TelegramMessageId: envelope.TelegramMessageId,
            ErrorCode: isBusinessAccepted ? null : finalProcessingStatus.ToString(),
            Message: finalMessage,
            NormalizedEnvelope: envelope,
            AuthorizedActivityCodes: groupAuth.AuthorizedActivityCodes,
            TelegramMessageLogId: ingestion.TelegramMessageLogId,
            PersistedProcessingStatus: finalProcessingStatus,
            ReceivedAtUtc: ingestion.ReceivedAtUtc,
            ActivityProcessingResult: activityResult,
            AcknowledgementStatus: ackDelivery.DeliveryStatus,
            ReplyTelegramMessageId: ackDelivery.ReplyTelegramMessageId,
            AcknowledgementDeliveryResult: ackDelivery);
    }

    public TelegramBotMaskedDiagnosticsDto GetMaskedConfigurationDiagnostics()
    {
        var options = _optionsMonitor.CurrentValue;
        var validation = TelegramBotOptionsValidator.ValidateConfiguration(
            options,
            requireCredentialsEvenWhenDisabled: true);

        return options.ToMaskedDiagnostics(validation);
    }

    private static bool IsAllWhitespace(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }
}
