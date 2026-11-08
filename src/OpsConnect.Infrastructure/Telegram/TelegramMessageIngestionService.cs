using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Common;
using OpsConnect.Application.Telegram;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Telegram;
using OpsConnect.Infrastructure.Persistence;

namespace OpsConnect.Infrastructure.Telegram;

/// <summary>
/// Atomically persists incoming Telegram messages to telegram.TelegramMessageLog with durable
/// database-backed duplicate detection on TelegramUpdateId and (TelegramChatId, TelegramMessageId).
/// Does NOT modify ActivityStatus or ActivityHistory (Task 5.2 boundary).
/// </summary>
public sealed class TelegramMessageIngestionService : ITelegramMessageIngestionService
{
    private const string SavepointName = "TelegramIngestSavepoint";
    private const int SqlDuplicateKeyRowError = 2601;
    private const int SqlUniqueConstraintError = 2627;

    private readonly OpsConnectDbContext _dbContext;
    private readonly ITelegramActivityMessageParser _messageParser;
    private readonly IBusinessClock _businessClock;
    private readonly IOptionsMonitor<TelegramBotOptions> _optionsMonitor;
    private readonly ILogger<TelegramMessageIngestionService> _logger;

    public TelegramMessageIngestionService(
        OpsConnectDbContext dbContext,
        ITelegramActivityMessageParser messageParser,
        IBusinessClock businessClock,
        IOptionsMonitor<TelegramBotOptions> optionsMonitor,
        ILogger<TelegramMessageIngestionService> logger)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(messageParser);
        ArgumentNullException.ThrowIfNull(businessClock);
        ArgumentNullException.ThrowIfNull(optionsMonitor);
        ArgumentNullException.ThrowIfNull(logger);

        _dbContext = dbContext;
        _messageParser = messageParser;
        _businessClock = businessClock;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
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
            // 1. Pre-check for existing duplicate record by UpdateId or (ChatId, MessageId)
            var existing = await FindExistingMessageLogAsync(
                envelope.UpdateId,
                envelope.TelegramChatId,
                envelope.TelegramMessageId,
                cancellationToken);

            if (existing is not null)
            {
                return new TelegramMessageIngestionResult(
                    IsPersistedOrDuplicate: true,
                    IsDuplicate: true,
                    TelegramMessageLogId: existing.TelegramMessageLogId,
                    ProcessingStatus: existing.ProcessingStatus,
                    ReceivedAtUtc: existing.ReceivedAtUtc,
                    ErrorCode: null,
                    ErrorMessage: null);
            }

            // 2. Build new TelegramMessageLog audit record
            var receivedAtUtc = _businessClock.UtcNow;
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
                ReceivedAtUtc = receivedAtUtc,
                ProcessedAtUtc = initialStatus == MessageProcessingStatus.RejectedUnauthorisedGroup ? receivedAtUtc : null,
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

            return await SaveLogEntryWithConcurrencyGuardAsync(logEntry, envelope, cancellationToken);
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
                ErrorMessage: "Temporary service error while persisting Telegram update; please retry.");
        }
    }

    private async Task<TelegramMessageIngestionResult> SaveLogEntryWithConcurrencyGuardAsync(
        TelegramMessageLog logEntry,
        NormalizedTelegramMessageEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var ambientTransaction = _dbContext.Database.CurrentTransaction;
        if (ambientTransaction is not null)
        {
            await ambientTransaction.CreateSavepointAsync(SavepointName, cancellationToken);
            try
            {
                _dbContext.TelegramMessageLogs.Add(logEntry);
                await _dbContext.SaveChangesAsync(cancellationToken);
                await ambientTransaction.ReleaseSavepointAsync(SavepointName, cancellationToken);

                return new TelegramMessageIngestionResult(
                    IsPersistedOrDuplicate: true,
                    IsDuplicate: false,
                    TelegramMessageLogId: logEntry.TelegramMessageLogId,
                    ProcessingStatus: logEntry.ProcessingStatus,
                    ReceivedAtUtc: logEntry.ReceivedAtUtc,
                    ErrorCode: null,
                    ErrorMessage: null);
            }
            catch (DbUpdateException dbEx) when (IsUniqueConstraintViolation(dbEx))
            {
                await ambientTransaction.RollbackToSavepointAsync(SavepointName, cancellationToken);
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
                        ErrorMessage: null);
                }

                throw;
            }
            catch
            {
                await ambientTransaction.RollbackToSavepointAsync(SavepointName, cancellationToken);
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
                        ErrorMessage: null);
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
                    ErrorMessage: null);
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
