using OpsConnect.Domain.Telegram;

namespace OpsConnect.Application.Telegram;

/// <summary>
/// High-level operational health status for integration components and the overall system.
/// </summary>
public enum IntegrationHealthStatus
{
    Healthy = 1,
    Degraded = 2,
    Unhealthy = 3,
    NotConfigured = 4
}

/// <summary>
/// Status report for an individual system or integration component.
/// </summary>
public sealed record TelegramComponentHealthDto(
    string ComponentName,
    IntegrationHealthStatus Status,
    string Description,
    long? LatencyMs = null,
    string? Error = null);

/// <summary>
/// Comprehensive health report covering application, database, and Telegram Bot API status.
/// </summary>
public sealed record TelegramHealthReportDto(
    DateTime CheckedAtUtc,
    IntegrationHealthStatus Status,
    string Description,
    IReadOnlyList<TelegramComponentHealthDto> Components);

/// <summary>
/// Bot configuration operational status summary.
/// </summary>
public sealed record TelegramBotConfigStatusDto(
    bool IsEnabled,
    bool HasBotToken,
    bool IsConfigurationValid,
    string StatusDescription,
    IReadOnlyList<string> ValidationErrors);

/// <summary>
/// Webhook endpoint operational status summary.
/// </summary>
public sealed record TelegramWebhookConfigStatusDto(
    string? ConfiguredWebhookUrl,
    bool HasWebhookSecretToken,
    bool? IsRegisteredWithTelegram,
    string? RegisteredWebhookUrl,
    int? PendingUpdateCount,
    DateTime? LastErrorDateUtc,
    string? LastErrorMessage);

/// <summary>
/// Database connectivity operational status summary.
/// </summary>
public sealed record DatabaseConnectivityStatusDto(
    bool IsConnected,
    long LatencyMs,
    IntegrationHealthStatus Status,
    string? ErrorMessage);

/// <summary>
/// Active Telegram group counts summary.
/// </summary>
public sealed record TelegramGroupCountsDto(
    int ActiveSourceGroupsCount,
    int ActiveDestinationGroupsCount);

/// <summary>
/// Most recent Telegram activity timestamps in UTC.
/// </summary>
public sealed record TelegramRecentActivityTimestampsDto(
    DateTime? LastWebhookReceiptAtUtc,
    DateTime? LastSuccessfullyProcessedAtUtc,
    DateTime? LastAcknowledgementDeliveredAtUtc);

/// <summary>
/// Outbound acknowledgement delivery metrics summary.
/// </summary>
public sealed record TelegramAcknowledgementMetricsDto(
    int PendingCount,
    int SendingCount,
    int FailedRetryableCount,
    int FailedPermanentCount,
    int TotalFailedCount,
    int DeliveredCount);

/// <summary>
/// Stale message and acknowledgement processing metrics.
/// </summary>
public sealed record TelegramStaleMetricsDto(
    int StaleProcessingMessageCount,
    int StaleSendingAcknowledgementCount,
    int StaleThresholdMinutes);

/// <summary>
/// Operational status report for administrator monitoring dashboards.
/// </summary>
public sealed record TelegramOperationalStatusDto(
    DateTime EvaluatedAtUtc,
    IntegrationHealthStatus OverallHealth,
    TelegramBotConfigStatusDto BotConfiguration,
    TelegramWebhookConfigStatusDto WebhookConfiguration,
    DatabaseConnectivityStatusDto DatabaseConnectivity,
    TelegramGroupCountsDto GroupCounts,
    TelegramRecentActivityTimestampsDto RecentActivity,
    TelegramAcknowledgementMetricsDto AcknowledgementMetrics,
    TelegramStaleMetricsDto StaleMetrics);

/// <summary>
/// Query filter parameters for operational diagnostics.
/// </summary>
public sealed record TelegramOperationalDiagnosticsQuery(
    int TimeWindowMinutes = 60,
    int RecentErrorsLimit = 10,
    string? ActivityCode = null,
    string? BranchCode = null)
{
    public const int MinTimeWindowMinutes = 5;
    public const int MaxTimeWindowMinutes = 10_080; // 7 days
    public const int DefaultTimeWindowMinutes = 60;
    public const int MinErrorsLimit = 1;
    public const int MaxErrorsLimit = 50;
    public const int DefaultErrorsLimit = 10;
}

/// <summary>
/// High-level message and acknowledgement count summary within a diagnostics time window.
/// </summary>
public sealed record TelegramDiagnosticsSummaryDto(
    int TotalMessagesReceived,
    int SuccessfullyProcessedCount,
    int BusinessRejectedCount,
    int TechnicalFailuresCount,
    int AcknowledgementDeliveryFailuresCount,
    int PendingAcknowledgementRetriesCount,
    int StaleProcessingCount,
    int StaleSendingCount);

/// <summary>
/// Sanitized record of a recent error for operational investigation.
/// Personal sender information, tokens, secrets, and stack traces are excluded.
/// </summary>
public sealed record TelegramRecentErrorSummaryDto(
    long TelegramMessageLogId,
    DateTime ReceivedAtUtc,
    long TelegramChatId,
    string? SourceGroupTitle,
    MessageProcessingStatus ProcessingStatus,
    AcknowledgementDeliveryStatus AcknowledgementStatus,
    string? ParsedActivityCode,
    string? ParsedBranchCode,
    string SanitizedErrorMessage,
    int AcknowledgementAttemptCount);

/// <summary>
/// Operational diagnostics response with metrics breakdown and recent error summaries.
/// </summary>
public sealed record TelegramOperationalDiagnosticsDto(
    DateTime EvaluatedAtUtc,
    int TimeWindowMinutes,
    DateTime WindowStartUtc,
    DateTime WindowEndUtc,
    TelegramDiagnosticsSummaryDto Summary,
    IReadOnlyDictionary<string, int> StatusBreakdown,
    IReadOnlyDictionary<string, int> AcknowledgementStatusBreakdown,
    IReadOnlyList<TelegramRecentErrorSummaryDto> RecentErrors);

/// <summary>
/// Webhook registration status information from Telegram Bot API.
/// </summary>
public sealed record TelegramWebhookInfoResult(
    bool IsSuccess,
    int? HttpStatusCode,
    string? ErrorMessage,
    bool IsConfigured,
    string? Url,
    bool? HasCustomCertificate,
    int? PendingUpdateCount,
    DateTime? LastErrorDateUtc,
    string? LastErrorMessage);
