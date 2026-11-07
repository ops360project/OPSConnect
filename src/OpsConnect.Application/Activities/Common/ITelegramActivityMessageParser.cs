using OpsConnect.Domain.Activities;

namespace OpsConnect.Application.Activities.Common;

public enum TelegramMessageParseErrorCode
{
    EmptyMessage = 1,
    UnrecognisedLine = 2,
    DuplicateField = 3,
    ConflictingField = 4,
    MissingRequiredField = 5,
    InvalidActivityCode = 6,
    InvalidBranchCode = 7,
    UnsupportedStatus = 8
}

public sealed record TelegramMessageParseError(
    int? LineNumber,
    string? FieldName,
    string? RawValue,
    TelegramMessageParseErrorCode ErrorCode,
    string Message);

public sealed record ParsedTelegramActivityMessage(
    string ActivityCode,
    string BranchCode,
    BranchActivityStatus Status,
    string RawActivityValue,
    string RawBranchValue,
    string RawStatusValue);

public sealed record TelegramMessageParseResult(
    bool IsSuccess,
    ParsedTelegramActivityMessage? ParsedMessage,
    string? PrimaryFailureReason,
    IReadOnlyList<TelegramMessageParseError> Errors);

/// <summary>
/// Standalone parser for Telegram operational activity update messages.
/// Independent of Telegram Bot API, HTTP transport, and database persistence.
/// </summary>
public interface ITelegramActivityMessageParser
{
    TelegramMessageParseResult Parse(string? rawMessageText);
}
