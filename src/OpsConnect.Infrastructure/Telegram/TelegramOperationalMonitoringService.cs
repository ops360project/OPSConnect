using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsConnect.Application.Common;
using OpsConnect.Application.Telegram;
using OpsConnect.Domain.Telegram;
using OpsConnect.Infrastructure.Persistence;

namespace OpsConnect.Infrastructure.Telegram;

/// <summary>
/// Implements operational health checks, dashboard status queries, and diagnostic investigation reports
/// for the OpsConnect Telegram Bot and Webhook infrastructure.
/// </summary>
public sealed class TelegramOperationalMonitoringService : ITelegramOperationalMonitoringService
{
    public const int DefaultStaleThresholdMinutes = 5;
    public const int MinTimeWindowMinutes = 5;
    public const int MaxTimeWindowMinutes = 10_080; // 7 days
    public const int DefaultTimeWindowMinutes = 60;
    public const int MinErrorsLimit = 1;
    public const int MaxErrorsLimit = 50;
    public const int DefaultErrorsLimit = 10;
    public const int MaxSanitizedErrorLength = 300;

    private readonly OpsConnectDbContext _dbContext;
    private readonly IOptionsMonitor<TelegramBotOptions> _optionsMonitor;
    private readonly ITelegramBotClient _telegramBotClient;
    private readonly IBusinessClock _clock;
    private readonly ILogger<TelegramOperationalMonitoringService> _logger;

    public TelegramOperationalMonitoringService(
        OpsConnectDbContext dbContext,
        IOptionsMonitor<TelegramBotOptions> optionsMonitor,
        ITelegramBotClient telegramBotClient,
        IBusinessClock clock,
        ILogger<TelegramOperationalMonitoringService> logger)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(optionsMonitor);
        ArgumentNullException.ThrowIfNull(telegramBotClient);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _dbContext = dbContext;
        _optionsMonitor = optionsMonitor;
        _telegramBotClient = telegramBotClient;
        _clock = clock;
        _logger = logger;
    }

    public async Task<TelegramHealthReportDto> CheckHealthAsync(
        bool verifyExternalTelegramApi = false,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = _clock.UtcNow;
        var components = new List<TelegramComponentHealthDto>();

        // 1. API Availability Component
        components.Add(new TelegramComponentHealthDto(
            ComponentName: "api",
            Status: IntegrationHealthStatus.Healthy,
            Description: "OpsConnect API service is active and responsive."));

        // 2. Database Connectivity Component
        var dbCheck = await CheckDatabaseConnectivityAsync(cancellationToken);
        components.Add(new TelegramComponentHealthDto(
            ComponentName: "database",
            Status: dbCheck.Status,
            Description: dbCheck.IsConnected
                ? $"Database is connected (latency: {dbCheck.LatencyMs}ms)."
                : "Database connectivity check failed.",
            LatencyMs: dbCheck.LatencyMs,
            Error: dbCheck.ErrorMessage));

        // 3. Telegram Configuration Component
        var options = _optionsMonitor.CurrentValue;
        var validation = TelegramBotOptionsValidator.ValidateConfiguration(options);
        var (configStatus, configDescription) = EvaluateConfigurationHealth(options, validation);
        components.Add(new TelegramComponentHealthDto(
            ComponentName: "telegram_configuration",
            Status: configStatus,
            Description: configDescription,
            Error: validation.IsValid ? null : string.Join("; ", validation.Errors)));

        // 4. Telegram Bot API Connectivity & Webhook Status
        var (apiStatus, apiDesc, apiLatency, apiError, webhookStatus, webhookDesc, _) =
            await EvaluateTelegramApiAndWebhookHealthAsync(options, configStatus, verifyExternalTelegramApi, cancellationToken);

        components.Add(new TelegramComponentHealthDto(
            ComponentName: "telegram_api",
            Status: apiStatus,
            Description: apiDesc,
            LatencyMs: apiLatency,
            Error: apiError));

        components.Add(new TelegramComponentHealthDto(
            ComponentName: "webhook",
            Status: webhookStatus,
            Description: webhookDesc));

        // Evaluate overall system and integration health
        var (overallStatus, overallDescription) = DetermineOverallHealth(dbCheck.Status, configStatus, apiStatus, webhookStatus);

        return new TelegramHealthReportDto(
            CheckedAtUtc: nowUtc,
            Status: overallStatus,
            Description: overallDescription,
            Components: components);
    }

    public async Task<TelegramOperationalStatusDto> GetOperationalStatusAsync(
        bool verifyExternalTelegramApi = false,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = _clock.UtcNow;
        var staleThreshold = nowUtc.AddMinutes(-DefaultStaleThresholdMinutes);
        var options = _optionsMonitor.CurrentValue;
        var validation = TelegramBotOptionsValidator.ValidateConfiguration(options);

        // Database connectivity evaluation
        var dbCheck = await CheckDatabaseConnectivityAsync(cancellationToken);

        // Telegram Bot & Webhook external or local evaluation
        var (configStatus, configDescription) = EvaluateConfigurationHealth(options, validation);
        var (apiStatus, _, _, _, webhookStatus, _, webhookInfo) =
            await EvaluateTelegramApiAndWebhookHealthAsync(options, configStatus, verifyExternalTelegramApi, cancellationToken);

        var (overallHealth, _) = DetermineOverallHealth(dbCheck.Status, configStatus, apiStatus, webhookStatus);

        // Group counts
        int activeSourceGroupsCount = 0;
        int activeDestGroupsCount = 0;

        DateTime? lastWebhookReceiptAtUtc = null;
        DateTime? lastSuccessfullyProcessedAtUtc = null;
        DateTime? lastAckDeliveredAtUtc = null;

        int pendingAckCount = 0;
        int sendingAckCount = 0;
        int failedRetryableAckCount = 0;
        int failedPermanentAckCount = 0;
        int deliveredAckCount = 0;

        int staleProcessingCount = 0;
        int staleSendingCount = 0;

        if (dbCheck.IsConnected)
        {
            try
            {
                activeSourceGroupsCount = await _dbContext.TelegramGroups
                    .AsNoTracking()
                    .CountAsync(g => g.IsActive && g.GroupRole == TelegramGroupRole.Source, cancellationToken);

                activeDestGroupsCount = await _dbContext.TelegramGroups
                    .AsNoTracking()
                    .CountAsync(g => g.IsActive && g.GroupRole == TelegramGroupRole.Destination, cancellationToken);

                lastWebhookReceiptAtUtc = await _dbContext.TelegramMessageLogs
                    .AsNoTracking()
                    .MaxAsync(m => (DateTime?)m.ReceivedAtUtc, cancellationToken);

                lastSuccessfullyProcessedAtUtc = await _dbContext.TelegramMessageLogs
                    .AsNoTracking()
                    .Where(m => m.ProcessingStatus == MessageProcessingStatus.Accepted ||
                                m.ProcessingStatus == MessageProcessingStatus.AcceptedUnchanged)
                    .MaxAsync(m => (DateTime?)m.ProcessedAtUtc, cancellationToken);

                lastAckDeliveredAtUtc = await _dbContext.TelegramMessageLogs
                    .AsNoTracking()
                    .Where(m => m.AcknowledgementStatus == AcknowledgementDeliveryStatus.Delivered)
                    .MaxAsync(m => (DateTime?)m.AcknowledgementDeliveredAtUtc, cancellationToken);

                pendingAckCount = await _dbContext.TelegramMessageLogs
                    .AsNoTracking()
                    .CountAsync(m => m.AcknowledgementStatus == AcknowledgementDeliveryStatus.Pending, cancellationToken);

                sendingAckCount = await _dbContext.TelegramMessageLogs
                    .AsNoTracking()
                    .CountAsync(m => m.AcknowledgementStatus == AcknowledgementDeliveryStatus.Sending, cancellationToken);

                failedRetryableAckCount = await _dbContext.TelegramMessageLogs
                    .AsNoTracking()
                    .CountAsync(m => m.AcknowledgementStatus == AcknowledgementDeliveryStatus.FailedRetryable, cancellationToken);

                failedPermanentAckCount = await _dbContext.TelegramMessageLogs
                    .AsNoTracking()
                    .CountAsync(m => m.AcknowledgementStatus == AcknowledgementDeliveryStatus.FailedPermanent, cancellationToken);

                deliveredAckCount = await _dbContext.TelegramMessageLogs
                    .AsNoTracking()
                    .CountAsync(m => m.AcknowledgementStatus == AcknowledgementDeliveryStatus.Delivered, cancellationToken);

                staleProcessingCount = await _dbContext.TelegramMessageLogs
                    .AsNoTracking()
                    .CountAsync(m => m.ProcessingStatus == MessageProcessingStatus.Processing && m.ReceivedAtUtc < staleThreshold, cancellationToken);

                staleSendingCount = await _dbContext.TelegramMessageLogs
                    .AsNoTracking()
                    .CountAsync(m => m.AcknowledgementStatus == AcknowledgementDeliveryStatus.Sending && m.AcknowledgementSendingStartedAtUtc < staleThreshold, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query Telegram operational statistics from database: {Message}", ex.Message);
            }
        }

        var botConfigStatus = new TelegramBotConfigStatusDto(
            IsEnabled: options.IsEnabled,
            HasBotToken: !TelegramSecretMasker.IsMissingOrPlaceholder(options.BotToken),
            IsConfigurationValid: validation.IsValid,
            StatusDescription: configDescription,
            ValidationErrors: validation.Errors);

        var webhookConfigStatus = new TelegramWebhookConfigStatusDto(
            ConfiguredWebhookUrl: string.IsNullOrWhiteSpace(options.WebhookUrl) ? null : options.WebhookUrl.Trim(),
            HasWebhookSecretToken: !TelegramSecretMasker.IsMissingOrPlaceholder(options.WebhookSecretToken),
            IsRegisteredWithTelegram: webhookInfo?.IsConfigured,
            RegisteredWebhookUrl: webhookInfo?.Url,
            PendingUpdateCount: webhookInfo?.PendingUpdateCount,
            LastErrorDateUtc: webhookInfo?.LastErrorDateUtc,
            LastErrorMessage: webhookInfo?.LastErrorMessage);

        var groupCounts = new TelegramGroupCountsDto(
            ActiveSourceGroupsCount: activeSourceGroupsCount,
            ActiveDestinationGroupsCount: activeDestGroupsCount);

        var recentActivity = new TelegramRecentActivityTimestampsDto(
            LastWebhookReceiptAtUtc: lastWebhookReceiptAtUtc,
            LastSuccessfullyProcessedAtUtc: lastSuccessfullyProcessedAtUtc,
            LastAcknowledgementDeliveredAtUtc: lastAckDeliveredAtUtc);

        var ackMetrics = new TelegramAcknowledgementMetricsDto(
            PendingCount: pendingAckCount,
            SendingCount: sendingAckCount,
            FailedRetryableCount: failedRetryableAckCount,
            FailedPermanentCount: failedPermanentAckCount,
            TotalFailedCount: failedRetryableAckCount + failedPermanentAckCount,
            DeliveredCount: deliveredAckCount);

        var staleMetrics = new TelegramStaleMetricsDto(
            StaleProcessingMessageCount: staleProcessingCount,
            StaleSendingAcknowledgementCount: staleSendingCount,
            StaleThresholdMinutes: DefaultStaleThresholdMinutes);

        return new TelegramOperationalStatusDto(
            EvaluatedAtUtc: nowUtc,
            OverallHealth: overallHealth,
            BotConfiguration: botConfigStatus,
            WebhookConfiguration: webhookConfigStatus,
            DatabaseConnectivity: dbCheck,
            GroupCounts: groupCounts,
            RecentActivity: recentActivity,
            AcknowledgementMetrics: ackMetrics,
            StaleMetrics: staleMetrics);
    }

    public async Task<TelegramOperationalDiagnosticsDto> GetOperationalDiagnosticsAsync(
        TelegramOperationalDiagnosticsQuery query,
        CancellationToken cancellationToken = default)
    {
        int timeWindow = Math.Clamp(query.TimeWindowMinutes, MinTimeWindowMinutes, MaxTimeWindowMinutes);
        int errorsLimit = Math.Clamp(query.RecentErrorsLimit, MinErrorsLimit, MaxErrorsLimit);

        var nowUtc = _clock.UtcNow;
        var windowStartUtc = nowUtc.AddMinutes(-timeWindow);
        var staleThreshold = nowUtc.AddMinutes(-DefaultStaleThresholdMinutes);

        var baseQuery = _dbContext.TelegramMessageLogs
            .AsNoTracking()
            .Where(m => m.ReceivedAtUtc >= windowStartUtc && m.ReceivedAtUtc <= nowUtc);

        if (!string.IsNullOrWhiteSpace(query.ActivityCode))
        {
            var normalizedAct = query.ActivityCode.Trim().ToUpperInvariant();
            baseQuery = baseQuery.Where(m => m.ParsedActivityCode == normalizedAct);
        }

        if (!string.IsNullOrWhiteSpace(query.BranchCode))
        {
            var normalizedBranch = query.BranchCode.Trim();
            baseQuery = baseQuery.Where(m => m.ParsedBranchCode == normalizedBranch);
        }

        var statusCounts = await baseQuery
            .GroupBy(m => m.ProcessingStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var ackCounts = await baseQuery
            .GroupBy(m => m.AcknowledgementStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int staleProcessingCount = await baseQuery
            .CountAsync(m => m.ProcessingStatus == MessageProcessingStatus.Processing && m.ReceivedAtUtc < staleThreshold, cancellationToken);

        int staleSendingCount = await baseQuery
            .CountAsync(m => m.AcknowledgementStatus == AcknowledgementDeliveryStatus.Sending && m.AcknowledgementSendingStartedAtUtc < staleThreshold, cancellationToken);

        var statusDict = statusCounts.ToDictionary(k => k.Status.ToString(), v => v.Count);
        var ackDict = ackCounts.ToDictionary(k => k.Status.ToString(), v => v.Count);

        int totalReceived = statusCounts.Sum(s => s.Count);

        int successfulCount = statusCounts
            .Where(s => s.Status is MessageProcessingStatus.Accepted or MessageProcessingStatus.AcceptedUnchanged)
            .Sum(s => s.Count);

        int businessRejectedCount = statusCounts
            .Where(s => s.Status is MessageProcessingStatus.RejectedInvalidFormat or
                                     MessageProcessingStatus.RejectedUnknownActivity or
                                     MessageProcessingStatus.RejectedUnknownBranch or
                                     MessageProcessingStatus.RejectedInactiveBranch or
                                     MessageProcessingStatus.RejectedInvalidTransition or
                                     MessageProcessingStatus.RejectedUnauthorisedGroup)
            .Sum(s => s.Count);

        int technicalFailuresCount = statusCounts
            .Where(s => s.Status is MessageProcessingStatus.FailedError)
            .Sum(s => s.Count);

        int failedRetryable = ackCounts
            .Where(a => a.Status == AcknowledgementDeliveryStatus.FailedRetryable)
            .Sum(a => a.Count);

        int failedPermanent = ackCounts
            .Where(a => a.Status == AcknowledgementDeliveryStatus.FailedPermanent)
            .Sum(a => a.Count);

        int pendingCount = ackCounts
            .Where(a => a.Status == AcknowledgementDeliveryStatus.Pending)
            .Sum(a => a.Count);

        int ackFailuresCount = failedRetryable + failedPermanent;
        int pendingRetriesCount = pendingCount + failedRetryable;

        var summary = new TelegramDiagnosticsSummaryDto(
            TotalMessagesReceived: totalReceived,
            SuccessfullyProcessedCount: successfulCount,
            BusinessRejectedCount: businessRejectedCount,
            TechnicalFailuresCount: technicalFailuresCount,
            AcknowledgementDeliveryFailuresCount: ackFailuresCount,
            PendingAcknowledgementRetriesCount: pendingRetriesCount,
            StaleProcessingCount: staleProcessingCount,
            StaleSendingCount: staleSendingCount);

        var recentErrorRows = await baseQuery
            .Where(m => m.ProcessingStatus == MessageProcessingStatus.FailedError ||
                        m.ProcessingStatus == MessageProcessingStatus.RejectedInvalidFormat ||
                        m.ProcessingStatus == MessageProcessingStatus.RejectedUnknownActivity ||
                        m.ProcessingStatus == MessageProcessingStatus.RejectedUnknownBranch ||
                        m.ProcessingStatus == MessageProcessingStatus.RejectedInactiveBranch ||
                        m.ProcessingStatus == MessageProcessingStatus.RejectedInvalidTransition ||
                        m.AcknowledgementStatus == AcknowledgementDeliveryStatus.FailedRetryable ||
                        m.AcknowledgementStatus == AcknowledgementDeliveryStatus.FailedPermanent)
            .OrderByDescending(m => m.ReceivedAtUtc)
            .Take(errorsLimit)
            .Select(m => new
            {
                m.TelegramMessageLogId,
                m.ReceivedAtUtc,
                m.TelegramChatId,
                m.SourceGroupTitle,
                m.ProcessingStatus,
                m.AcknowledgementStatus,
                m.ParsedActivityCode,
                m.ParsedBranchCode,
                RawError = m.RejectionReason ?? m.AcknowledgementLastError ?? "Processing error",
                m.AcknowledgementAttemptCount
            })
            .ToListAsync(cancellationToken);

        var recentErrors = recentErrorRows
            .Select(r => new TelegramRecentErrorSummaryDto(
                TelegramMessageLogId: r.TelegramMessageLogId,
                ReceivedAtUtc: r.ReceivedAtUtc,
                TelegramChatId: r.TelegramChatId,
                SourceGroupTitle: r.SourceGroupTitle,
                ProcessingStatus: r.ProcessingStatus,
                AcknowledgementStatus: r.AcknowledgementStatus,
                ParsedActivityCode: r.ParsedActivityCode,
                ParsedBranchCode: r.ParsedBranchCode,
                SanitizedErrorMessage: SanitizeErrorMessage(r.RawError),
                AcknowledgementAttemptCount: r.AcknowledgementAttemptCount))
            .ToList();

        return new TelegramOperationalDiagnosticsDto(
            EvaluatedAtUtc: nowUtc,
            TimeWindowMinutes: timeWindow,
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: nowUtc,
            Summary: summary,
            StatusBreakdown: statusDict,
            AcknowledgementStatusBreakdown: ackDict,
            RecentErrors: recentErrors);
    }

    private async Task<DatabaseConnectivityStatusDto> CheckDatabaseConnectivityAsync(
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        bool canConnect = false;
        string? error = null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            canConnect = await _dbContext.Database.CanConnectAsync(cts.Token);
        }
        catch (Exception ex)
        {
            error = TelegramSecretMasker.SanitizeForLogging(ex.Message);
            _logger.LogWarning(ex, "Database connectivity check failed: {SanitizedError}", error);
        }

        sw.Stop();
        if (!canConnect && string.IsNullOrWhiteSpace(error))
        {
            error = "Unable to establish connection to SQL Server database.";
        }

        var status = canConnect ? IntegrationHealthStatus.Healthy : IntegrationHealthStatus.Unhealthy;

        return new DatabaseConnectivityStatusDto(
            IsConnected: canConnect,
            LatencyMs: sw.ElapsedMilliseconds,
            Status: status,
            ErrorMessage: error);
    }

    private static (IntegrationHealthStatus Status, string Description) EvaluateConfigurationHealth(
        TelegramBotOptions options,
        TelegramConfigurationValidationResult validation)
    {
        if (!options.IsEnabled || TelegramSecretMasker.IsMissingOrPlaceholder(options.BotToken))
        {
            return (
                IntegrationHealthStatus.NotConfigured,
                options.IsEnabled
                    ? "Telegram BotToken is missing or contains placeholder values."
                    : "Telegram bot integration is currently disabled in configuration.");
        }

        if (!validation.IsValid)
        {
            return (
                IntegrationHealthStatus.Degraded,
                $"Telegram bot configuration has validation errors: {string.Join("; ", validation.Errors)}");
        }

        return (
            IntegrationHealthStatus.Healthy,
            "Telegram bot configuration is valid and active.");
    }

    private async Task<(
        IntegrationHealthStatus ApiStatus,
        string ApiDesc,
        long? ApiLatencyMs,
        string? ApiError,
        IntegrationHealthStatus WebhookStatus,
        string WebhookDesc,
        TelegramWebhookInfoResult? WebhookInfo)> EvaluateTelegramApiAndWebhookHealthAsync(
        TelegramBotOptions options,
        IntegrationHealthStatus configStatus,
        bool verifyExternalApi,
        CancellationToken cancellationToken)
    {
        if (configStatus == IntegrationHealthStatus.NotConfigured)
        {
            return (
                IntegrationHealthStatus.NotConfigured,
                "Telegram Bot API is not configured.",
                null,
                null,
                IntegrationHealthStatus.NotConfigured,
                "Telegram webhook is not configured.",
                null);
        }

        if (!verifyExternalApi)
        {
            bool hasWebhookUrl = !string.IsNullOrWhiteSpace(options.WebhookUrl);
            bool hasWebhookSecret = !TelegramSecretMasker.IsMissingOrPlaceholder(options.WebhookSecretToken);

            var localWebhookStatus = (hasWebhookUrl && hasWebhookSecret)
                ? IntegrationHealthStatus.Healthy
                : IntegrationHealthStatus.Degraded;

            var localWebhookDesc = localWebhookStatus == IntegrationHealthStatus.Healthy
                ? "Webhook URL and secret token are configured locally."
                : "Webhook URL or secret token is missing/placeholder in configuration.";

            return (
                IntegrationHealthStatus.Healthy,
                "Telegram Bot API configuration is active (external API verification skipped; set verifyExternalApi=true to verify).",
                null,
                null,
                localWebhookStatus,
                localWebhookDesc,
                null);
        }

        // Verify outbound connection to Telegram Bot API
        var sw = Stopwatch.StartNew();
        TelegramWebhookInfoResult? webhookInfo = null;
        string? apiError = null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            webhookInfo = await _telegramBotClient.GetWebhookInfoAsync(cts.Token);
        }
        catch (Exception ex)
        {
            apiError = TelegramSecretMasker.SanitizeForLogging(ex.Message, options.BotToken, options.WebhookSecretToken);
            _logger.LogWarning(ex, "Failed to check Telegram getWebhookInfo: {SanitizedError}", apiError);
        }

        sw.Stop();
        long latencyMs = sw.ElapsedMilliseconds;

        if (webhookInfo is not null && webhookInfo.IsSuccess)
        {
            string apiDesc = $"Outbound connectivity to Telegram Bot API verified ({latencyMs}ms).";
            IntegrationHealthStatus webhookStatus;
            string webhookDesc;

            if (webhookInfo.IsConfigured)
            {
                webhookStatus = IntegrationHealthStatus.Healthy;
                webhookDesc = $"Webhook registered with Telegram. Pending updates: {webhookInfo.PendingUpdateCount ?? 0}.";
            }
            else
            {
                webhookStatus = IntegrationHealthStatus.Degraded;
                webhookDesc = "Webhook URL is not currently registered on Telegram Bot API.";
            }

            return (
                IntegrationHealthStatus.Healthy,
                apiDesc,
                latencyMs,
                null,
                webhookStatus,
                webhookDesc,
                webhookInfo);
        }

        string failReason = webhookInfo?.ErrorMessage ?? apiError ?? "Failed to connect to Telegram Bot API.";
        return (
            IntegrationHealthStatus.Degraded,
            $"Telegram Bot API connectivity check failed: {failReason}",
            latencyMs,
            failReason,
            IntegrationHealthStatus.Degraded,
            "Unable to verify webhook registration status due to Telegram Bot API connectivity failure.",
            webhookInfo);
    }

    private static (IntegrationHealthStatus OverallStatus, string Description) DetermineOverallHealth(
        IntegrationHealthStatus dbStatus,
        IntegrationHealthStatus configStatus,
        IntegrationHealthStatus apiStatus,
        IntegrationHealthStatus webhookStatus)
    {
        if (dbStatus == IntegrationHealthStatus.Unhealthy)
        {
            return (
                IntegrationHealthStatus.Unhealthy,
                "Critical failure: SQL Server database is unreachable.");
        }

        if (configStatus == IntegrationHealthStatus.NotConfigured)
        {
            return (
                IntegrationHealthStatus.NotConfigured,
                "Application and database are operational; Telegram bot is not configured.");
        }

        if (configStatus == IntegrationHealthStatus.Degraded ||
            apiStatus == IntegrationHealthStatus.Degraded ||
            webhookStatus == IntegrationHealthStatus.Degraded)
        {
            return (
                IntegrationHealthStatus.Degraded,
                "Application and database are operational; Telegram bot integration is operating with degraded capabilities.");
        }

        return (
            IntegrationHealthStatus.Healthy,
            "Application, database, and Telegram bot integration components are fully healthy.");
    }

    private string SanitizeErrorMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Unspecified error.";
        }

        var options = _optionsMonitor.CurrentValue;
        var sanitized = TelegramSecretMasker.SanitizeForLogging(text.Trim(), options.BotToken, options.WebhookSecretToken);

        // Strip stack traces or internal exception prefixes if present
        if (sanitized.Contains("at System.", StringComparison.OrdinalIgnoreCase) ||
            sanitized.Contains("at Microsoft.", StringComparison.OrdinalIgnoreCase))
        {
            int atIdx = sanitized.IndexOf("at ", StringComparison.OrdinalIgnoreCase);
            if (atIdx > 0)
            {
                sanitized = sanitized[..atIdx].Trim();
            }
        }

        return sanitized.Length <= MaxSanitizedErrorLength
            ? sanitized
            : sanitized[..MaxSanitizedErrorLength];
    }
}
