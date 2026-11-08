using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Domain.Telegram;

namespace OpsConnect.Application.Telegram;

/// <summary>
/// Configuration options for the OpsConnect Telegram Bot and Webhook infrastructure.
/// Secrets (BotToken, WebhookSecretToken) must be supplied via User Secrets or environment variables
/// and must never be hardcoded or committed to source control.
/// </summary>
public sealed class TelegramBotOptions
{
    public const string SectionName = "TelegramBot";
    public const string SecretTokenHeaderName = "X-Telegram-Bot-Api-Secret-Token";
    public const int DefaultMaxWebhookPayloadBytes = 65_536;
    public const int MinWebhookPayloadBytes = 1_024;
    public const int MaxAllowedWebhookPayloadBytes = 262_144;

    public bool IsEnabled { get; set; }

    public string? BotToken { get; set; }

    public string? WebhookUrl { get; set; }

    public string? WebhookSecretToken { get; set; }

    public int MaxWebhookPayloadBytes { get; set; } = DefaultMaxWebhookPayloadBytes;

    public List<long> AuthorizedSourceChatIds { get; set; } = [];

    public List<long> DestinationChatIds { get; set; } = [];

    public TelegramBotMaskedDiagnosticsDto ToMaskedDiagnostics(TelegramConfigurationValidationResult validation) =>
        new(
            IsEnabled: IsEnabled,
            HasBotToken: !TelegramSecretMasker.IsMissingOrPlaceholder(BotToken),
            MaskedBotToken: TelegramSecretMasker.MaskSecret(BotToken),
            WebhookUrl: string.IsNullOrWhiteSpace(WebhookUrl) ? null : WebhookUrl.Trim(),
            HasWebhookSecretToken: !TelegramSecretMasker.IsMissingOrPlaceholder(WebhookSecretToken),
            MaskedWebhookSecretToken: TelegramSecretMasker.MaskSecret(WebhookSecretToken),
            MaxWebhookPayloadBytes: MaxWebhookPayloadBytes,
            AuthorizedSourceChatIds: AuthorizedSourceChatIds.Distinct().OrderBy(id => id).ToArray(),
            DestinationChatIds: DestinationChatIds.Distinct().OrderBy(id => id).ToArray(),
            IsConfigurationValidForEnabledBot: validation.IsValid,
            ValidationErrors: validation.Errors);
}

public sealed record TelegramBotMaskedDiagnosticsDto(
    bool IsEnabled,
    bool HasBotToken,
    string? MaskedBotToken,
    string? WebhookUrl,
    bool HasWebhookSecretToken,
    string? MaskedWebhookSecretToken,
    int MaxWebhookPayloadBytes,
    IReadOnlyList<long> AuthorizedSourceChatIds,
    IReadOnlyList<long> DestinationChatIds,
    bool IsConfigurationValidForEnabledBot,
    IReadOnlyList<string> ValidationErrors);

public sealed record TelegramConfigurationValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors);

/// <summary>
/// Utility for masking Telegram Bot tokens, webhook secret tokens, and sensitive URLs in logs and API responses.
/// </summary>
public static partial class TelegramSecretMasker
{
    public const string MaskedPlaceholder = "***MASKED***";

    [GeneratedRegex(@"bot\d+:[A-Za-z0-9_-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BotTokenUrlSegmentRegex();

    public static bool IsMissingOrPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        return trimmed.StartsWith('<') ||
               trimmed.EndsWith('>') ||
               trimmed.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("YOUR_", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals(MaskedPlaceholder, StringComparison.OrdinalIgnoreCase);
    }

    public static string? MaskSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        return MaskedPlaceholder;
    }

    public static string SanitizeForLogging(string? message, params string?[] knownSecrets)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var sanitized = BotTokenUrlSegmentRegex().Replace(message, $"bot{MaskedPlaceholder}");

        if (knownSecrets is not null)
        {
            foreach (var secret in knownSecrets)
            {
                if (!string.IsNullOrWhiteSpace(secret) && secret.Trim().Length >= 4)
                {
                    sanitized = sanitized.Replace(secret.Trim(), MaskedPlaceholder, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        return sanitized;
    }
}

/// <summary>
/// Represents the top-level JSON structure of an incoming Telegram Bot API Update payload.
/// </summary>
public sealed class TelegramWebhookUpdatePayload
{
    [JsonPropertyName("update_id")]
    public long? UpdateId { get; set; }

    [JsonPropertyName("message")]
    public TelegramWebhookMessagePayload? Message { get; set; }

    [JsonPropertyName("edited_message")]
    public TelegramWebhookMessagePayload? EditedMessage { get; set; }

    [JsonPropertyName("channel_post")]
    public TelegramWebhookMessagePayload? ChannelPost { get; set; }

    [JsonPropertyName("edited_channel_post")]
    public TelegramWebhookMessagePayload? EditedChannelPost { get; set; }

    [JsonPropertyName("callback_query")]
    public object? CallbackQuery { get; set; }

    [JsonPropertyName("my_chat_member")]
    public object? MyChatMember { get; set; }

    [JsonPropertyName("chat_member")]
    public object? ChatMember { get; set; }
}

public sealed class TelegramWebhookMessagePayload
{
    [JsonPropertyName("message_id")]
    public long? MessageId { get; set; }

    [JsonPropertyName("date")]
    public long? DateUnixSeconds { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("caption")]
    public string? Caption { get; set; }

    [JsonPropertyName("chat")]
    public TelegramWebhookChatPayload? Chat { get; set; }

    [JsonPropertyName("from")]
    public TelegramWebhookUserPayload? From { get; set; }
}

public sealed class TelegramWebhookChatPayload
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

public sealed class TelegramWebhookUserPayload
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("is_bot")]
    public bool? IsBot { get; set; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

/// <summary>
/// Normalized, validated Telegram message envelope ready for group authorisation and downstream activity processing.
/// Captures Open Group Mode sender metadata without requiring employee registration or mobile-number mapping.
/// </summary>
public sealed record NormalizedTelegramMessageEnvelope(
    long UpdateId,
    long TelegramChatId,
    string ChatType,
    string? ChatTitle,
    long TelegramMessageId,
    long? TelegramUserId,
    bool IsSenderBot,
    string? SenderUsername,
    string? SenderDisplayName,
    DateTime MessageTimestampUtc,
    string RawMessageText);

public enum TelegramUpdateHandlingOutcome
{
    AcceptedForProcessing = 1,
    IgnoredNonMessageUpdate = 2,
    IgnoredBotDisabled = 3,
    RejectedUnauthorizedSecret = 4,
    RejectedOversizedPayload = 5,
    RejectedMalformedPayload = 6,
    RejectedUnauthorizedSourceGroup = 7,
    DuplicateIgnored = 8,
    FailedPersistence = 9,
    ProcessedSuccessfully = 10,
    RejectedActivitySubmission = 11,
    ProcessingFailed = 12,
    AcknowledgementDeliveryFailed = 13
}

public sealed record TelegramUpdateNormalizationResult(
    bool IsValid,
    bool IsTextMessageCandidate,
    string? UpdateCategory,
    string? ErrorReason,
    NormalizedTelegramMessageEnvelope? Envelope)
{
    public static TelegramUpdateNormalizationResult ValidTextMessage(NormalizedTelegramMessageEnvelope envelope) =>
        new(
            IsValid: true,
            IsTextMessageCandidate: true,
            UpdateCategory: "message",
            ErrorReason: null,
            Envelope: envelope);

    public static TelegramUpdateNormalizationResult NonMessageUpdate(string updateCategory, string reason) =>
        new(
            IsValid: true,
            IsTextMessageCandidate: false,
            UpdateCategory: updateCategory,
            ErrorReason: reason,
            Envelope: null);

    public static TelegramUpdateNormalizationResult InvalidPayload(string errorReason) =>
        new(
            IsValid: false,
            IsTextMessageCandidate: false,
            UpdateCategory: null,
            ErrorReason: errorReason,
            Envelope: null);
}

public sealed record TelegramGroupAuthorizationResult(
    bool IsAuthorized,
    long TelegramChatId,
    TelegramGroupRole? GroupRole,
    string? AuthorizationSource,
    IReadOnlyList<string> AuthorizedActivityCodes,
    string? RejectionReason,
    int? SourceTelegramGroupId = null,
    string? SourceGroupTitle = null,
    int? DefaultActivityId = null);

public sealed record TelegramMessageIngestionResult(
    bool IsPersistedOrDuplicate,
    bool IsDuplicate,
    long? TelegramMessageLogId,
    MessageProcessingStatus? ProcessingStatus,
    DateTime? ReceivedAtUtc,
    string? ErrorCode,
    string? ErrorMessage,
    bool RequiresActivityProcessing = false,
    bool RequiresAcknowledgementDelivery = false);

public sealed record TelegramMessageActivityProcessingResult(
    bool IsCompleted,
    bool IsDuplicateOrAlreadyProcessed,
    long TelegramMessageLogId,
    MessageProcessingStatus FinalProcessingStatus,
    DateTime? ProcessedAtUtc,
    ActivityUpdateProcessingResult? ActivityResult,
    string? FailureReason);

public sealed record TelegramAcknowledgementDeliveryResult(
    bool IsDelivered,
    bool WasAlreadyDeliveredOrInFlight,
    bool IsRetryableFailure,
    long TelegramMessageLogId,
    AcknowledgementDeliveryStatus DeliveryStatus,
    long? ReplyTelegramMessageId,
    DateTime? DeliveredAtUtc,
    int AttemptCount,
    string? AcknowledgementText,
    string? ErrorCode,
    string? ErrorMessage,
    int? RetryAfterSeconds = null);

public sealed record TelegramWebhookHandlingResult(
    bool IsSuccess,
    TelegramUpdateHandlingOutcome Outcome,
    int RecommendedHttpStatusCode,
    long? UpdateId,
    long? TelegramChatId,
    long? TelegramMessageId,
    string? ErrorCode,
    string Message,
    NormalizedTelegramMessageEnvelope? NormalizedEnvelope = null,
    IReadOnlyList<string>? AuthorizedActivityCodes = null,
    long? TelegramMessageLogId = null,
    MessageProcessingStatus? PersistedProcessingStatus = null,
    DateTime? ReceivedAtUtc = null,
    ActivityUpdateProcessingResult? ActivityProcessingResult = null,
    AcknowledgementDeliveryStatus? AcknowledgementStatus = null,
    long? ReplyTelegramMessageId = null,
    TelegramAcknowledgementDeliveryResult? AcknowledgementDeliveryResult = null);

public sealed record TelegramBotSendResult(
    bool IsSuccess,
    long? SentTelegramMessageId,
    int? HttpStatusCode,
    string? ErrorMessage,
    bool IsTransientFailure = false,
    int? RetryAfterSeconds = null,
    string? ErrorCode = null);

/// <summary>
/// Formats concise, group-safe Telegram submission acknowledgement replies without exposing
/// SQL errors, stack traces, internal exception types, configuration secrets, or personal sender data.
/// </summary>
public static class TelegramAcknowledgementFormatter
{
    public const int MaxAcknowledgementLength = 500;

    private static readonly string[] InternalErrorMarkers =
    [
        "SqlException",
        "DbUpdateException",
        "DbUpdateConcurrencyException",
        "StackTrace",
        "System.Data",
        "Microsoft.Data.SqlClient",
        "Microsoft.EntityFrameworkCore",
        "   at "
    ];

    public static string FormatForResult(
        ActivityUpdateProcessingResult? activityResult,
        MessageProcessingStatus finalStatus,
        TelegramBotOptions? options = null)
    {
        var candidate = activityResult?.ReplyMessage;
        if (string.IsNullOrWhiteSpace(candidate) || ContainsInternalDiagnosticMarkers(candidate))
        {
            candidate = BuildFallbackMessage(activityResult, finalStatus);
        }

        var sanitized = TelegramSecretMasker.SanitizeForLogging(
            candidate.Trim(),
            options?.BotToken,
            options?.WebhookSecretToken);

        return sanitized.Length <= MaxAcknowledgementLength
            ? sanitized
            : sanitized[..MaxAcknowledgementLength];
    }

    public static bool ContainsInternalDiagnosticMarkers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var marker in InternalErrorMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildFallbackMessage(
        ActivityUpdateProcessingResult? result,
        MessageProcessingStatus status)
    {
        var activityCode = string.IsNullOrWhiteSpace(result?.ActivityCode) ? "RFFO" : result.ActivityCode;
        var branchLabel = string.IsNullOrWhiteSpace(result?.BranchCode) ? string.Empty : $" Branch {result.BranchCode}";
        var dateLabel = result?.ReportingDate.HasValue == true
            ? $" ({result.ReportingDate.Value:dd-MMM-yyyy})"
            : string.Empty;

        return status switch
        {
            MessageProcessingStatus.Accepted when result?.Outcome == OpsConnect.Domain.Activities.TransitionOutcome.AcceptedViaReopen =>
                $"[{activityCode}]{branchLabel}{dateLabel}: Status reopened from {result.PreviousStatus} to {result.CurrentStatus}.",
            MessageProcessingStatus.Accepted =>
                $"[{activityCode}]{branchLabel}{dateLabel}: Status updated from {result?.PreviousStatus} to {result?.CurrentStatus}.",
            MessageProcessingStatus.AcceptedUnchanged =>
                $"[{activityCode}]{branchLabel}{dateLabel}: Status is already {result?.CurrentStatus} (no change).",
            MessageProcessingStatus.RejectedInvalidFormat =>
                "Update rejected (Invalid Format): Please submit Activity, Branch Code, and Status in the required 3-line format.",
            MessageProcessingStatus.RejectedUnknownActivity =>
                $"Update rejected: Activity '{activityCode}' is not supported or is currently inactive.",
            MessageProcessingStatus.RejectedUnknownBranch =>
                $"Update rejected:{branchLabel} is not registered in Organisation Master.",
            MessageProcessingStatus.RejectedInactiveBranch =>
                $"Update rejected:{branchLabel} is currently inactive or unmapped for the reporting date.",
            MessageProcessingStatus.RejectedInvalidTransition =>
                $"[{activityCode}]{branchLabel}{dateLabel}: Update rejected. Status transition is not permitted.",
            _ =>
                "Activity submission could not be processed."
        };
    }
}
