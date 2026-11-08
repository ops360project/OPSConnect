using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Common;
using OpsConnect.Application.Telegram;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Telegram;
using OpsConnect.Infrastructure.Persistence;

namespace OpsConnect.Infrastructure.Telegram;

/// <summary>
/// Atomically persists incoming Telegram messages to telegram.TelegramMessageLog with durable
/// database-backed duplicate detection on TelegramUpdateId and (TelegramChatId, TelegramMessageId),
/// and coordinates activity processing via IActivityUpdateProcessor with RowVersion concurrency protection
/// and crash/retry recovery.
/// </summary>
public sealed class TelegramMessageIngestionService : ITelegramMessageIngestionService
{
    private const string IngestSavepointName = "TelegramIngestSavepoint";
    private const string ClaimSavepointName = "TelegramClaimSavepoint";
    private const string CompleteSavepointName = "TelegramCompleteSavepoint";
    private const int SqlDuplicateKeyRowError = 2601;
    private const int SqlUniqueConstraintError = 2627;

    public static readonly TimeSpan DefaultStaleProcessingTimeout = TimeSpan.FromSeconds(60);

    private readonly OpsConnectDbContext _dbContext;
    private readonly ITelegramActivityMessageParser _messageParser;
    private readonly IActivityUpdateProcessor _activityUpdateProcessor;
    private readonly IBusinessClock _businessClock;
    private readonly IOptionsMonitor<TelegramBotOptions> _optionsMonitor;
    private readonly ILogger<TelegramMessageIngestionService> _logger;
    private readonly TimeSpan _staleProcessingTimeout;

    public TelegramMessageIngestionService(
        OpsConnectDbContext dbContext,
        ITelegramActivityMessageParser messageParser,
        IActivityUpdateProcessor activityUpdateProcessor,
        IBusinessClock businessClock,
        IOptionsMonitor<TelegramBotOptions> optionsMonitor,
        ILogger<TelegramMessageIngestionService> logger,
        TimeSpan? staleProcessingTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(messageParser);
        ArgumentNullException.ThrowIfNull(activityUpdateProcessor);
        ArgumentNullException.ThrowIfNull(businessClock);
        ArgumentNullException.ThrowIfNull(optionsMonitor);
        ArgumentNullException.ThrowIfNull(logger);

        _dbContext = dbContext;
        _messageParser = messageParser;
        _activityUpdateProcessor = activityUpdateProcessor;
        _businessClock = businessClock;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
        _staleProcessingTimeout = staleProcessingTimeout ?? DefaultStaleProcessingTimeout;
    }

    public Task<TelegramMessageIngestionResult> IngestAuthorizedMessageAsync(
        NormalizedTelegramMessageEnvelope envelope,
        TelegramGroupAuthorizationResult groupAuthorization,
        CancellationToken cancellationToken = default) =>
        PersistMessageLogCoreAsync(
            envelope,
            groupAuthorization,
            initialStatus: MessageProcessingStatus.Received,
            rejectionReason: null,
            cancellationToken);

    public Task<TelegramMessageIngestionResult> RecordRejectedGroupMessageAsync(
        NormalizedTelegramMessageEnvelope envelope,
        TelegramGroupAuthorizationResult groupAuthorization,
        CancellationToken cancellationToken = default) =>
        PersistMessageLogCoreAsync(
            envelope,
            groupAuthorization,
            initialStatus: MessageProcessingStatus.RejectedUnauthorisedGroup,
            rejectionReason: groupAuthorization.RejectionReason ?? $"Telegram chat '{envelope.TelegramChatId}' is not an authorised source group.",
            cancellationToken);

    public async Task<TelegramMessageActivityProcessingResult> ProcessIngestedMessageAsync(
        long telegramMessageLogId,
        NormalizedTelegramMessageEnvelope envelope,
        TelegramGroupAuthorizationResult groupAuthorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(groupAuthorization);

        var nowUtc = _businessClock.UtcNow;

        try
        {
            var log = await _dbContext.TelegramMessageLogs
                .SingleOrDefaultAsync(x => x.TelegramMessageLogId == telegramMessageLogId, cancellationToken);

            if (log is null)
            {
                return new TelegramMessageActivityProcessingResult(
                    IsCompleted: false,
                    IsDuplicateOrAlreadyProcessed: false,
                    TelegramMessageLogId: telegramMessageLogId,
                    FinalProcessingStatus: MessageProcessingStatus.FailedError,
                    ProcessedAtUtc: null,
                    ActivityResult: null,
                    FailureReason: "Persisted Telegram message log record was not found.");
            }

            // 1. If already terminally processed, do not process again
            if (log.IsTerminallyProcessed)
            {
                return new TelegramMessageActivityProcessingResult(
                    IsCompleted: true,
                    IsDuplicateOrAlreadyProcessed: true,
                    TelegramMessageLogId: log.TelegramMessageLogId,
                    FinalProcessingStatus: log.ProcessingStatus,
                    ProcessedAtUtc: log.ProcessedAtUtc,
                    ActivityResult: null,
                    FailureReason: null);
            }

            // 2. If another concurrent worker is actively processing within the lease window, do not double-process
            if (log.IsActivelyProcessing(nowUtc, _staleProcessingTimeout))
            {
                return new TelegramMessageActivityProcessingResult(
                    IsCompleted: true,
                    IsDuplicateOrAlreadyProcessed: true,
                    TelegramMessageLogId: log.TelegramMessageLogId,
                    FinalProcessingStatus: MessageProcessingStatus.Processing,
                    ProcessedAtUtc: null,
                    ActivityResult: null,
                    FailureReason: null);
            }

            // 3. Crash-recovery reconciliation: check if ActivityHistory was already committed for this TelegramMessageLogId
            var existingHistory = await _dbContext.ActivityHistories
                .AsNoTracking()
                .SingleOrDefaultAsync(h => h.TelegramMessageLogId == log.TelegramMessageLogId, cancellationToken);

            if (existingHistory is not null)
            {
                var reconciledStatus = existingHistory.Outcome switch
                {
                    TransitionOutcome.Accepted or TransitionOutcome.AcceptedViaReopen => MessageProcessingStatus.Accepted,
                    TransitionOutcome.AcceptedUnchanged => MessageProcessingStatus.AcceptedUnchanged,
                    _ => MessageProcessingStatus.RejectedInvalidTransition
                };

                log.MarkCompleted(
                    finalStatus: reconciledStatus,
                    processedAtUtc: existingHistory.RecordedAtUtc,
                    activityId: existingHistory.ActivityId,
                    branchId: existingHistory.BranchId,
                    reportingDate: existingHistory.ReportingDate,
                    parsedActivityCode: log.ParsedActivityCode,
                    parsedBranchCode: log.ParsedBranchCode,
                    parsedStatusText: existingHistory.NewStatus.ToString(),
                    rejectionReason: reconciledStatus == MessageProcessingStatus.RejectedInvalidTransition
                        ? existingHistory.Notes
                        : null);

                await SaveChangesInSavepointOrTransactionAsync(CompleteSavepointName, cancellationToken);

                return new TelegramMessageActivityProcessingResult(
                    IsCompleted: true,
                    IsDuplicateOrAlreadyProcessed: true,
                    TelegramMessageLogId: log.TelegramMessageLogId,
                    FinalProcessingStatus: log.ProcessingStatus,
                    ProcessedAtUtc: log.ProcessedAtUtc,
                    ActivityResult: null,
                    FailureReason: null);
            }

            // 4. Claim the TelegramMessageLog row for processing using RowVersion optimistic concurrency
            bool claimed = await TryClaimMessageForProcessingAsync(log, nowUtc, cancellationToken);
            if (!claimed)
            {
                var reloaded = await _dbContext.TelegramMessageLogs
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.TelegramMessageLogId == telegramMessageLogId, cancellationToken);

                return new TelegramMessageActivityProcessingResult(
                    IsCompleted: true,
                    IsDuplicateOrAlreadyProcessed: true,
                    TelegramMessageLogId: telegramMessageLogId,
                    FinalProcessingStatus: reloaded?.ProcessingStatus ?? MessageProcessingStatus.Processing,
                    ProcessedAtUtc: reloaded?.ProcessedAtUtc,
                    ActivityResult: null,
                    FailureReason: null);
            }

            // 5. Invoke IActivityUpdateProcessor (which parses and delegates to RffoActivityUpdateHandler)
            var effectiveReportingDate = log.ReportingDate
                ?? _businessClock.GetBusinessDate(ActivityReportingConfiguration.DefaultTimeZoneId);

            var submissionRequest = new RawActivityMessageSubmissionRequest(
                RawMessageText: log.RawMessageText,
                TelegramMessageLogId: log.TelegramMessageLogId,
                TelegramChatId: log.TelegramChatId,
                TelegramMessageId: log.TelegramMessageId,
                TelegramUserId: log.TelegramUserId,
                SenderUsername: log.SenderUsername,
                SenderDisplayName: log.SenderDisplayName,
                OccurredAtUtc: nowUtc,
                ExplicitReportingDate: effectiveReportingDate);

            var activityResult = await _activityUpdateProcessor.ProcessRawMessageAsync(
                submissionRequest,
                cancellationToken);

            // If the handler returned DuplicateIgnored because a concurrent worker already inserted ActivityHistory
            if (activityResult.ProcessingStatus == MessageProcessingStatus.DuplicateIgnored)
            {
                var completedAt = _businessClock.UtcNow;
                log.MarkCompleted(
                    finalStatus: MessageProcessingStatus.Accepted,
                    processedAtUtc: completedAt,
                    activityId: activityResult.ActivityId,
                    branchId: activityResult.BranchId,
                    reportingDate: activityResult.ReportingDate,
                    parsedActivityCode: activityResult.ActivityCode,
                    parsedBranchCode: activityResult.BranchCode,
                    parsedStatusText: log.ParsedStatusText,
                    rejectionReason: null);

                await SaveChangesInSavepointOrTransactionAsync(CompleteSavepointName, cancellationToken);

                return new TelegramMessageActivityProcessingResult(
                    IsCompleted: true,
                    IsDuplicateOrAlreadyProcessed: true,
                    TelegramMessageLogId: log.TelegramMessageLogId,
                    FinalProcessingStatus: log.ProcessingStatus,
                    ProcessedAtUtc: log.ProcessedAtUtc,
                    ActivityResult: activityResult,
                    FailureReason: null);
            }

            // 6. Persist the final processing outcome on the original TelegramMessageLog record
            var processedAtUtc = _businessClock.UtcNow;
            var statusText = (activityResult.CurrentStatus ?? activityResult.PreviousStatus)?.ToString() ?? log.ParsedStatusText;

            log.MarkCompleted(
                finalStatus: activityResult.ProcessingStatus,
                processedAtUtc: processedAtUtc,
                activityId: activityResult.ActivityId,
                branchId: activityResult.BranchId,
                reportingDate: activityResult.ReportingDate,
                parsedActivityCode: Truncate(activityResult.ActivityCode, 50),
                parsedBranchCode: Truncate(activityResult.BranchCode, 50),
                parsedStatusText: Truncate(statusText, 50),
                rejectionReason: Truncate(activityResult.RejectionReason, 1000));

            await SaveChangesInSavepointOrTransactionAsync(CompleteSavepointName, cancellationToken);

            return new TelegramMessageActivityProcessingResult(
                IsCompleted: true,
                IsDuplicateOrAlreadyProcessed: false,
                TelegramMessageLogId: log.TelegramMessageLogId,
                FinalProcessingStatus: log.ProcessingStatus,
                ProcessedAtUtc: log.ProcessedAtUtc,
                ActivityResult: activityResult,
                FailureReason: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var options = _optionsMonitor.CurrentValue;
            var sanitized = TelegramSecretMasker.SanitizeForLogging(
                ex.Message,
                options.BotToken,
                options.WebhookSecretToken);

            _logger.LogError(
                "Failed to process ingested Telegram message (TelegramMessageLogId={LogId}): {SanitizedError}",
                telegramMessageLogId,
                sanitized);

            await TryMarkMessageFailedAsync(telegramMessageLogId, cancellationToken);

            return new TelegramMessageActivityProcessingResult(
                IsCompleted: false,
                IsDuplicateOrAlreadyProcessed: false,
                TelegramMessageLogId: telegramMessageLogId,
                FinalProcessingStatus: MessageProcessingStatus.FailedError,
                ProcessedAtUtc: null,
                ActivityResult: null,
                FailureReason: "Temporary service error while processing activity update; please retry.");
        }
    }

    private async Task<TelegramMessageIngestionResult> PersistMessageLogCoreAsync(
        NormalizedTelegramMessageEnvelope envelope,
        TelegramGroupAuthorizationResult groupAuthorization,
        MessageProcessingStatus initialStatus,
        string? rejectionReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(groupAuthorization);

        try
        {
            var nowUtc = _businessClock.UtcNow;

            // 1. Pre-check for existing duplicate record by UpdateId or (ChatId, MessageId)
            var existing = await FindExistingMessageLogAsync(
                envelope.UpdateId,
                envelope.TelegramChatId,
                envelope.TelegramMessageId,
                cancellationToken);

            if (existing is not null)
            {
                bool needsRecovery = initialStatus == MessageProcessingStatus.Received &&
                                     existing.CanStartOrRecoverProcessing(nowUtc, _staleProcessingTimeout);

                return new TelegramMessageIngestionResult(
                    IsPersistedOrDuplicate: true,
                    IsDuplicate: !needsRecovery,
                    TelegramMessageLogId: existing.TelegramMessageLogId,
                    ProcessingStatus: existing.ProcessingStatus,
                    ReceivedAtUtc: existing.ReceivedAtUtc,
                    ErrorCode: null,
                    ErrorMessage: null,
                    RequiresActivityProcessing: needsRecovery);
            }

            // 2. Build new TelegramMessageLog audit record
            var reportingDate = _businessClock.GetBusinessDate(ActivityReportingConfiguration.DefaultTimeZoneId);

            string? parsedActivityCode = null;
            string? parsedBranchCode = null;
            string? parsedStatusText = null;

            var parseResult = _messageParser.Parse(envelope.RawMessageText);
            if (parseResult.IsSuccess && parseResult.ParsedMessage is not null)
            {
                parsedActivityCode = Truncate(parseResult.ParsedMessage.ActivityCode, 50);
                parsedBranchCode = Truncate(parseResult.ParsedMessage.BranchCode, 50);
                parsedStatusText = Truncate(parseResult.ParsedMessage.Status.ToString(), 50);
            }

            var groupTitle = groupAuthorization.SourceGroupTitle ?? envelope.ChatTitle;

            var logEntry = new TelegramMessageLog
            {
                TelegramUpdateId = envelope.UpdateId,
                TelegramChatId = envelope.TelegramChatId,
                TelegramMessageId = envelope.TelegramMessageId,
                SourceTelegramGroupId = groupAuthorization.SourceTelegramGroupId,
                SourceGroupTitle = Truncate(groupTitle, 250),
                TelegramUserId = envelope.TelegramUserId,
                SenderUsername = Truncate(envelope.SenderUsername, 150),
                SenderDisplayName = Truncate(envelope.SenderDisplayName, 200),
                MessageTimestampUtc = envelope.MessageTimestampUtc,
                ReceivedAtUtc = nowUtc,
                ProcessedAtUtc = initialStatus == MessageProcessingStatus.RejectedUnauthorisedGroup ? nowUtc : null,
                RawMessageText = Truncate(envelope.RawMessageText, 4000) ?? string.Empty,
                ParsedActivityCode = parsedActivityCode,
                ParsedBranchCode = parsedBranchCode,
                ParsedStatusText = parsedStatusText,
                ActivityId = groupAuthorization.DefaultActivityId,
                BranchId = null,
                ReportingDate = reportingDate,
                ProcessingStatus = initialStatus,
                RejectionReason = Truncate(rejectionReason, 1000)
            };

            return await SaveLogEntryWithConcurrencyGuardAsync(
                logEntry,
                envelope,
                requiresActivityProcessing: initialStatus == MessageProcessingStatus.Received,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var options = _optionsMonitor.CurrentValue;
            var sanitized = TelegramSecretMasker.SanitizeForLogging(
                ex.Message,
                options.BotToken,
                options.WebhookSecretToken);

            _logger.LogError(
                "Failed to persist Telegram message (UpdateId={UpdateId}, ChatId={ChatId}, MessageId={MessageId}): {SanitizedError}",
                envelope.UpdateId,
                envelope.TelegramChatId,
                envelope.TelegramMessageId,
                sanitized);

            return new TelegramMessageIngestionResult(
                IsPersistedOrDuplicate: false,
                IsDuplicate: false,
                TelegramMessageLogId: null,
                ProcessingStatus: null,
                ReceivedAtUtc: null,
                ErrorCode: "DatabasePersistenceFailure",
                ErrorMessage: "Temporary service error while persisting Telegram update; please retry.",
                RequiresActivityProcessing: false);
        }
    }

    private async Task<bool> TryClaimMessageForProcessingAsync(
        TelegramMessageLog log,
        DateTime startedAtUtc,
        CancellationToken cancellationToken)
    {
        log.MarkProcessing(startedAtUtc);

        try
        {
            await SaveChangesInSavepointOrTransactionAsync(ClaimSavepointName, cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            _dbContext.Entry(log).State = EntityState.Detached;
            return false;
        }
    }

    private async Task TryMarkMessageFailedAsync(
        long telegramMessageLogId,
        CancellationToken cancellationToken)
    {
        try
        {
            _dbContext.ChangeTracker.Clear();

            var log = await _dbContext.TelegramMessageLogs
                .SingleOrDefaultAsync(x => x.TelegramMessageLogId == telegramMessageLogId, cancellationToken);

            if (log is not null && !log.IsTerminallyProcessed)
            {
                log.MarkProcessingFailed("Temporary processing error; eligible for retry recovery.");
                await SaveChangesInSavepointOrTransactionAsync(CompleteSavepointName, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Could not update TelegramMessageLog {LogId} status to FailedError after processing failure: {Reason}",
                telegramMessageLogId,
                ex.GetType().Name);
        }
    }

    private async Task SaveChangesInSavepointOrTransactionAsync(
        string savepointName,
        CancellationToken cancellationToken)
    {
        var ambientTransaction = _dbContext.Database.CurrentTransaction;
        if (ambientTransaction is not null)
        {
            await ambientTransaction.CreateSavepointAsync(savepointName, cancellationToken);
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                await ambientTransaction.ReleaseSavepointAsync(savepointName, cancellationToken);
                return;
            }
            catch
            {
                await ambientTransaction.RollbackToSavepointAsync(savepointName, cancellationToken);
                throw;
            }
        }

        var strategy = _dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        });
    }

    private async Task<TelegramMessageIngestionResult> SaveLogEntryWithConcurrencyGuardAsync(
        TelegramMessageLog logEntry,
        NormalizedTelegramMessageEnvelope envelope,
        bool requiresActivityProcessing,
        CancellationToken cancellationToken)
    {
        var ambientTransaction = _dbContext.Database.CurrentTransaction;
        if (ambientTransaction is not null)
        {
            await ambientTransaction.CreateSavepointAsync(IngestSavepointName, cancellationToken);
            try
            {
                _dbContext.TelegramMessageLogs.Add(logEntry);
                await _dbContext.SaveChangesAsync(cancellationToken);
                await ambientTransaction.ReleaseSavepointAsync(IngestSavepointName, cancellationToken);

                return new TelegramMessageIngestionResult(
                    IsPersistedOrDuplicate: true,
                    IsDuplicate: false,
                    TelegramMessageLogId: logEntry.TelegramMessageLogId,
                    ProcessingStatus: logEntry.ProcessingStatus,
                    ReceivedAtUtc: logEntry.ReceivedAtUtc,
                    ErrorCode: null,
                    ErrorMessage: null,
                    RequiresActivityProcessing: requiresActivityProcessing);
            }
            catch (DbUpdateException dbEx) when (IsUniqueConstraintViolation(dbEx))
            {
                await ambientTransaction.RollbackToSavepointAsync(IngestSavepointName, cancellationToken);
                _dbContext.Entry(logEntry).State = EntityState.Detached;

                var winner = await FindExistingMessageLogAsync(
                    envelope.UpdateId,
                    envelope.TelegramChatId,
                    envelope.TelegramMessageId,
                    cancellationToken);

                if (winner is not null)
                {
                    return new TelegramMessageIngestionResult(
                        IsPersistedOrDuplicate: true,
                        IsDuplicate: true,
                        TelegramMessageLogId: winner.TelegramMessageLogId,
                        ProcessingStatus: winner.ProcessingStatus,
                        ReceivedAtUtc: winner.ReceivedAtUtc,
                        ErrorCode: null,
                        ErrorMessage: null,
                        RequiresActivityProcessing: false);
                }

                throw;
            }
            catch
            {
                await ambientTransaction.RollbackToSavepointAsync(IngestSavepointName, cancellationToken);
                _dbContext.Entry(logEntry).State = EntityState.Detached;
                throw;
            }
        }

        var strategy = _dbContext.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    _dbContext.TelegramMessageLogs.Add(logEntry);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    return new TelegramMessageIngestionResult(
                        IsPersistedOrDuplicate: true,
                        IsDuplicate: false,
                        TelegramMessageLogId: logEntry.TelegramMessageLogId,
                        ProcessingStatus: logEntry.ProcessingStatus,
                        ReceivedAtUtc: logEntry.ReceivedAtUtc,
                        ErrorCode: null,
                        ErrorMessage: null,
                        RequiresActivityProcessing: requiresActivityProcessing);
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    _dbContext.Entry(logEntry).State = EntityState.Detached;
                    throw;
                }
            });
        }
        catch (DbUpdateException dbEx) when (IsUniqueConstraintViolation(dbEx))
        {
            _dbContext.Entry(logEntry).State = EntityState.Detached;

            var winner = await FindExistingMessageLogAsync(
                envelope.UpdateId,
                envelope.TelegramChatId,
                envelope.TelegramMessageId,
                cancellationToken);

            if (winner is not null)
            {
                return new TelegramMessageIngestionResult(
                    IsPersistedOrDuplicate: true,
                    IsDuplicate: true,
                    TelegramMessageLogId: winner.TelegramMessageLogId,
                    ProcessingStatus: winner.ProcessingStatus,
                    ReceivedAtUtc: winner.ReceivedAtUtc,
                    ErrorCode: null,
                    ErrorMessage: null,
                    RequiresActivityProcessing: false);
            }

            throw;
        }
    }

    private Task<TelegramMessageLog?> FindExistingMessageLogAsync(
        long updateId,
        long chatId,
        long messageId,
        CancellationToken cancellationToken) =>
        _dbContext.TelegramMessageLogs
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.TelegramUpdateId == updateId ||
                     (m.TelegramChatId == chatId && m.TelegramMessageId == messageId),
                cancellationToken);

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        if (exception.InnerException is SqlException sqlEx)
        {
            foreach (SqlError error in sqlEx.Errors)
            {
                if (error.Number is SqlDuplicateKeyRowError or SqlUniqueConstraintError)
                {
                    return true;
                }
            }
        }

        var message = exception.InnerException?.Message ?? exception.Message;
        return message.Contains("UX_TelegramMessageLog_TelegramUpdateId", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("UX_TelegramMessageLog_ChatId_MessageId", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
