using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Common;

namespace OpsConnect.Domain.Telegram;

public enum TelegramGroupRole
{
    Source = 1,
    Destination = 2
}

public enum MessageProcessingStatus
{
    Received = 1,
    Accepted = 2,
    AcceptedUnchanged = 3,
    RejectedInvalidFormat = 4,
    RejectedUnauthorisedGroup = 5,
    RejectedUnknownActivity = 6,
    RejectedUnknownBranch = 7,
    RejectedInactiveBranch = 8,
    RejectedInvalidTransition = 9,
    DuplicateIgnored = 10,
    FailedError = 11,
    Processing = 12
}

public enum AcknowledgementDeliveryStatus
{
    NotApplicable = 0,
    Pending = 1,
    Sending = 2,
    Delivered = 3,
    FailedRetryable = 4,
    FailedPermanent = 5
}

/// <summary>
/// Represents an authorised Telegram group mapped to an operational activity as either
/// a Source group (for branch submissions) or a Destination group (for summary reports).
/// </summary>
public class TelegramGroup : ActivatableAuditableEntity
{
    public int TelegramGroupId { get; set; }
    public int ActivityId { get; set; }
    public long TelegramChatId { get; set; }
    public string GroupTitle { get; set; } = string.Empty;
    public TelegramGroupRole GroupRole { get; set; }

    public ActivityMaster Activity { get; set; } = null!;
}

/// <summary>
/// Complete audit record of every incoming Telegram message.
/// Duplicate detection uses both TelegramUpdateId (when present) and the composite unique key (TelegramChatId, TelegramMessageId).
/// </summary>
public class TelegramMessageLog
{
    public long TelegramMessageLogId { get; set; }
    public long? TelegramUpdateId { get; set; }
    public long TelegramChatId { get; set; }
    public long TelegramMessageId { get; set; }

    public int? SourceTelegramGroupId { get; set; }
    public string? SourceGroupTitle { get; set; }

    public long? TelegramUserId { get; set; }
    public string? SenderUsername { get; set; }
    public string? SenderDisplayName { get; set; }

    public DateTime MessageTimestampUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessingStartedAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }

    public string RawMessageText { get; set; } = string.Empty;
    public string? ParsedActivityCode { get; set; }
    public string? ParsedBranchCode { get; set; }
    public string? ParsedStatusText { get; set; }

    public int? ActivityId { get; set; }
    public int? BranchId { get; set; }
    public DateOnly? ReportingDate { get; set; }

    public MessageProcessingStatus ProcessingStatus { get; set; } = MessageProcessingStatus.Received;
    public string? RejectionReason { get; set; }

    public AcknowledgementDeliveryStatus AcknowledgementStatus { get; set; } = AcknowledgementDeliveryStatus.NotApplicable;
    public string? AcknowledgementMessageText { get; set; }
    public int AcknowledgementAttemptCount { get; set; }
    public DateTime? AcknowledgementSendingStartedAtUtc { get; set; }
    public DateTime? AcknowledgementDeliveredAtUtc { get; set; }
    public string? AcknowledgementLastError { get; set; }
    public int? AcknowledgementRetryAfterSeconds { get; set; }
    public long? ReplyTelegramMessageId { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public bool IsTerminallyProcessed =>
        ProcessingStatus is
            MessageProcessingStatus.Accepted or
            MessageProcessingStatus.AcceptedUnchanged or
            MessageProcessingStatus.RejectedInvalidFormat or
            MessageProcessingStatus.RejectedUnauthorisedGroup or
            MessageProcessingStatus.RejectedUnknownActivity or
            MessageProcessingStatus.RejectedUnknownBranch or
            MessageProcessingStatus.RejectedInactiveBranch or
            MessageProcessingStatus.RejectedInvalidTransition;

    public bool RequiresAcknowledgement =>
        IsTerminallyProcessed &&
        ProcessingStatus != MessageProcessingStatus.RejectedUnauthorisedGroup;

    public bool IsActivelyProcessing(DateTime utcNow, TimeSpan staleProcessingTimeout) =>
        ProcessingStatus == MessageProcessingStatus.Processing &&
        ProcessingStartedAtUtc.HasValue &&
        utcNow - ProcessingStartedAtUtc.Value < staleProcessingTimeout;

    public bool CanStartOrRecoverProcessing(DateTime utcNow, TimeSpan staleProcessingTimeout) =>
        ProcessingStatus is MessageProcessingStatus.Received or MessageProcessingStatus.FailedError ||
        (ProcessingStatus == MessageProcessingStatus.Processing &&
         (!ProcessingStartedAtUtc.HasValue || utcNow - ProcessingStartedAtUtc.Value >= staleProcessingTimeout));

    public bool IsAcknowledgementActivelySending(DateTime utcNow, TimeSpan staleSendingTimeout) =>
        AcknowledgementStatus == AcknowledgementDeliveryStatus.Sending &&
        AcknowledgementSendingStartedAtUtc.HasValue &&
        utcNow - AcknowledgementSendingStartedAtUtc.Value < staleSendingTimeout;

    public bool CanSendOrRecoverAcknowledgement(DateTime utcNow, TimeSpan staleSendingTimeout) =>
        RequiresAcknowledgement &&
        (AcknowledgementStatus is AcknowledgementDeliveryStatus.Pending or AcknowledgementDeliveryStatus.FailedRetryable ||
         (AcknowledgementStatus == AcknowledgementDeliveryStatus.Sending &&
          (!AcknowledgementSendingStartedAtUtc.HasValue || utcNow - AcknowledgementSendingStartedAtUtc.Value >= staleSendingTimeout)));

    public void MarkProcessing(DateTime startedAtUtc)
    {
        ProcessingStatus = MessageProcessingStatus.Processing;
        ProcessingStartedAtUtc = startedAtUtc;
        RejectionReason = null;
    }

    public void MarkCompleted(
        MessageProcessingStatus finalStatus,
        DateTime processedAtUtc,
        int? activityId,
        int? branchId,
        DateOnly? reportingDate,
        string? parsedActivityCode,
        string? parsedBranchCode,
        string? parsedStatusText,
        string? rejectionReason,
        string? acknowledgementMessageText = null)
    {
        ProcessingStatus = finalStatus;
        ProcessingStartedAtUtc = null;
        ProcessedAtUtc = processedAtUtc;
        ActivityId = activityId ?? ActivityId;
        BranchId = branchId ?? BranchId;
        ReportingDate = reportingDate ?? ReportingDate;
        ParsedActivityCode = parsedActivityCode ?? ParsedActivityCode;
        ParsedBranchCode = parsedBranchCode ?? ParsedBranchCode;
        ParsedStatusText = parsedStatusText ?? ParsedStatusText;
        RejectionReason = string.IsNullOrWhiteSpace(rejectionReason) ? null : rejectionReason.Trim();

        if (!string.IsNullOrWhiteSpace(acknowledgementMessageText))
        {
            AcknowledgementMessageText = acknowledgementMessageText.Trim();
        }

        if (RequiresAcknowledgement && AcknowledgementStatus != AcknowledgementDeliveryStatus.Delivered)
        {
            AcknowledgementStatus = AcknowledgementDeliveryStatus.Pending;
        }
    }

    public void MarkProcessingFailed(string sanitizedFailureReason)
    {
        ProcessingStatus = MessageProcessingStatus.FailedError;
        ProcessingStartedAtUtc = null;
        ProcessedAtUtc = null;
        RejectionReason = string.IsNullOrWhiteSpace(sanitizedFailureReason)
            ? "Temporary processing error; eligible for retry recovery."
            : sanitizedFailureReason.Trim();
    }

    public void MarkAcknowledgementSending(DateTime startedAtUtc)
    {
        AcknowledgementStatus = AcknowledgementDeliveryStatus.Sending;
        AcknowledgementSendingStartedAtUtc = startedAtUtc;
        AcknowledgementAttemptCount++;
        AcknowledgementLastError = null;
        AcknowledgementRetryAfterSeconds = null;
    }

    public void MarkAcknowledgementDelivered(long? replyTelegramMessageId, DateTime deliveredAtUtc)
    {
        AcknowledgementStatus = AcknowledgementDeliveryStatus.Delivered;
        ReplyTelegramMessageId = replyTelegramMessageId ?? ReplyTelegramMessageId;
        AcknowledgementDeliveredAtUtc = deliveredAtUtc;
        AcknowledgementSendingStartedAtUtc = null;
        AcknowledgementLastError = null;
        AcknowledgementRetryAfterSeconds = null;
    }

    public void MarkAcknowledgementFailedRetryable(string? sanitizedError, int? retryAfterSeconds = null)
    {
        AcknowledgementStatus = AcknowledgementDeliveryStatus.FailedRetryable;
        AcknowledgementSendingStartedAtUtc = null;
        AcknowledgementRetryAfterSeconds = retryAfterSeconds is > 0 ? retryAfterSeconds : null;
        AcknowledgementLastError = string.IsNullOrWhiteSpace(sanitizedError)
            ? "Temporary error while sending Telegram acknowledgement; eligible for retry."
            : sanitizedError.Trim();
    }

    public void MarkAcknowledgementFailedPermanent(string? sanitizedError)
    {
        AcknowledgementStatus = AcknowledgementDeliveryStatus.FailedPermanent;
        AcknowledgementSendingStartedAtUtc = null;
        AcknowledgementRetryAfterSeconds = null;
        AcknowledgementLastError = string.IsNullOrWhiteSpace(sanitizedError)
            ? "Permanent error while sending Telegram acknowledgement."
            : sanitizedError.Trim();
    }
}
