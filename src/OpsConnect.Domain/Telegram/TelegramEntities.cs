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
    FailedError = 11
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
    public long? ReplyTelegramMessageId { get; set; }
}
