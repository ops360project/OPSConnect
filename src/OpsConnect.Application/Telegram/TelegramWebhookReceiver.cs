using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OpsConnect.Application.Telegram;

/// <summary>
/// Coordinates Telegram webhook security verification, payload size enforcement, JSON deserialization,
/// update normalisation, and Open Group Mode source-group authorisation.
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

    public TelegramWebhookReceiver(
        IOptionsMonitor<TelegramBotOptions> optionsMonitor,
        ITelegramUpdateNormalizer updateNormalizer,
        ITelegramGroupAuthorizationService groupAuthorizationService)
    {
        ArgumentNullException.ThrowIfNull(optionsMonitor);
        ArgumentNullException.ThrowIfNull(updateNormalizer);
        ArgumentNullException.ThrowIfNull(groupAuthorizationService);

        _optionsMonitor = optionsMonitor;
        _updateNormalizer = updateNormalizer;
        _groupAuthorizationService = groupAuthorizationService;
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

        // 6. Open Group Mode source-group authorisation (checks authorised group Chat ID; any member inside is permitted)
        var groupAuth = await _groupAuthorizationService.AuthorizeSourceGroupAsync(
            envelope.TelegramChatId,
            cancellationToken);

        if (!groupAuth.IsAuthorized)
        {
            return new TelegramWebhookHandlingResult(
                IsSuccess: false,
                Outcome: TelegramUpdateHandlingOutcome.RejectedUnauthorizedSourceGroup,
                RecommendedHttpStatusCode: 403,
                UpdateId: envelope.UpdateId,
                TelegramChatId: envelope.TelegramChatId,
                TelegramMessageId: envelope.TelegramMessageId,
                ErrorCode: "UnauthorizedSourceGroup",
                Message: groupAuth.RejectionReason ?? $"Telegram chat '{envelope.TelegramChatId}' is not an authorised source group.");
        }

        return new TelegramWebhookHandlingResult(
            IsSuccess: true,
            Outcome: TelegramUpdateHandlingOutcome.AcceptedForProcessing,
            RecommendedHttpStatusCode: 200,
            UpdateId: envelope.UpdateId,
            TelegramChatId: envelope.TelegramChatId,
            TelegramMessageId: envelope.TelegramMessageId,
            ErrorCode: null,
            Message: "Telegram update validated and source group authorised.",
            NormalizedEnvelope: envelope,
            AuthorizedActivityCodes: groupAuth.AuthorizedActivityCodes);
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
