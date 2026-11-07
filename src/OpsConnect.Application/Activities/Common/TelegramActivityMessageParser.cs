using System.Text.RegularExpressions;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Organisation;

namespace OpsConnect.Application.Activities.Common;

public sealed partial class TelegramActivityMessageParser : ITelegramActivityMessageParser
{
    public const string ActivityFieldName = "Activity";
    public const string BranchIdFieldName = "BranchID";
    public const string StatusFieldName = "Status";

    // Matches a field key (letters, spaces, underscores) up to the FIRST '-' or ':' separator,
    // preserving the entire remainder of the line (including any colons in branch codes like '36:01') as the value.
    [GeneratedRegex(@"^\s*(?<key>[A-Za-z][A-Za-z\s_]*?)\s*[-:]\s*(?<value>.*?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueLineRegex();

    [GeneratedRegex(@"[\s_]+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceAndUnderscoreRegex();

    public TelegramMessageParseResult Parse(string? rawMessageText)
    {
        if (string.IsNullOrWhiteSpace(rawMessageText))
        {
            var emptyError = new TelegramMessageParseError(
                LineNumber: null,
                FieldName: "Message",
                RawValue: rawMessageText,
                ErrorCode: TelegramMessageParseErrorCode.EmptyMessage,
                Message: "Message is empty. Expected Activity, BranchID, and Status fields.");

            return Failure(new[] { emptyError });
        }

        var errors = new List<TelegramMessageParseError>();
        var lines = rawMessageText.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

        var firstFieldOccurrences = new Dictionary<string, (int LineNumber, string RawValue)>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < lines.Length; index++)
        {
            int lineNumber = index + 1;
            var rawLine = lines[index];

            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var match = KeyValueLineRegex().Match(rawLine);
            if (!match.Success)
            {
                errors.Add(new TelegramMessageParseError(
                    LineNumber: lineNumber,
                    FieldName: null,
                    RawValue: rawLine.Trim(),
                    ErrorCode: TelegramMessageParseErrorCode.UnrecognisedLine,
                    Message: $"Line {lineNumber} ('{rawLine.Trim()}') is not in a valid 'Field- Value' format."));
                continue;
            }

            var rawKey = match.Groups["key"].Value.Trim();
            var rawValue = match.Groups["value"].Value.Trim();
            var canonicalField = ResolveCanonicalFieldName(rawKey);

            if (canonicalField is null)
            {
                errors.Add(new TelegramMessageParseError(
                    LineNumber: lineNumber,
                    FieldName: rawKey,
                    RawValue: rawLine.Trim(),
                    ErrorCode: TelegramMessageParseErrorCode.UnrecognisedLine,
                    Message: $"Line {lineNumber} contains unrecognised field '{rawKey}'. Only Activity, BranchID, and Status are permitted."));
                continue;
            }

            if (firstFieldOccurrences.TryGetValue(canonicalField, out var existing))
            {
                bool isConflicting = !string.Equals(existing.RawValue, rawValue, StringComparison.OrdinalIgnoreCase);
                errors.Add(new TelegramMessageParseError(
                    LineNumber: lineNumber,
                    FieldName: canonicalField,
                    RawValue: rawValue,
                    ErrorCode: isConflicting
                        ? TelegramMessageParseErrorCode.ConflictingField
                        : TelegramMessageParseErrorCode.DuplicateField,
                    Message: isConflicting
                        ? $"Field '{canonicalField}' on line {lineNumber} ('{rawValue}') conflicts with line {existing.LineNumber} ('{existing.RawValue}')."
                        : $"Duplicate field '{canonicalField}' on line {lineNumber} (already provided on line {existing.LineNumber})."));
                continue;
            }

            firstFieldOccurrences[canonicalField] = (lineNumber, rawValue);
        }

        // Verify all three mandatory fields are present
        string? normalizedActivityCode = null;
        string? normalizedBranchCode = null;
        BranchActivityStatus? parsedStatus = null;

        if (!firstFieldOccurrences.TryGetValue(ActivityFieldName, out var activityEntry))
        {
            errors.Add(new TelegramMessageParseError(
                LineNumber: null,
                FieldName: ActivityFieldName,
                RawValue: null,
                ErrorCode: TelegramMessageParseErrorCode.MissingRequiredField,
                Message: "Required field 'Activity' is missing."));
        }
        else if (string.IsNullOrWhiteSpace(activityEntry.RawValue))
        {
            errors.Add(new TelegramMessageParseError(
                LineNumber: activityEntry.LineNumber,
                FieldName: ActivityFieldName,
                RawValue: activityEntry.RawValue,
                ErrorCode: TelegramMessageParseErrorCode.InvalidActivityCode,
                Message: $"Activity code on line {activityEntry.LineNumber} cannot be empty."));
        }
        else
        {
            try
            {
                normalizedActivityCode = ActivityMaster.ValidateActivityCode(activityEntry.RawValue);
            }
            catch (ArgumentException ex)
            {
                errors.Add(new TelegramMessageParseError(
                    LineNumber: activityEntry.LineNumber,
                    FieldName: ActivityFieldName,
                    RawValue: activityEntry.RawValue,
                    ErrorCode: TelegramMessageParseErrorCode.InvalidActivityCode,
                    Message: ex.Message));
            }
        }

        if (!firstFieldOccurrences.TryGetValue(BranchIdFieldName, out var branchEntry))
        {
            errors.Add(new TelegramMessageParseError(
                LineNumber: null,
                FieldName: BranchIdFieldName,
                RawValue: null,
                ErrorCode: TelegramMessageParseErrorCode.MissingRequiredField,
                Message: "Required field 'BranchID' is missing."));
        }
        else if (string.IsNullOrWhiteSpace(branchEntry.RawValue))
        {
            errors.Add(new TelegramMessageParseError(
                LineNumber: branchEntry.LineNumber,
                FieldName: BranchIdFieldName,
                RawValue: branchEntry.RawValue,
                ErrorCode: TelegramMessageParseErrorCode.InvalidBranchCode,
                Message: $"BranchID on line {branchEntry.LineNumber} cannot be empty."));
        }
        else
        {
            try
            {
                normalizedBranchCode = OrganisationCodeNormalizer.NormalizeAndValidateBranchCode(
                    branchEntry.RawValue,
                    BranchIdFieldName);
            }
            catch (ArgumentException ex)
            {
                errors.Add(new TelegramMessageParseError(
                    LineNumber: branchEntry.LineNumber,
                    FieldName: BranchIdFieldName,
                    RawValue: branchEntry.RawValue,
                    ErrorCode: TelegramMessageParseErrorCode.InvalidBranchCode,
                    Message: ex.Message));
            }
        }

        if (!firstFieldOccurrences.TryGetValue(StatusFieldName, out var statusEntry))
        {
            errors.Add(new TelegramMessageParseError(
                LineNumber: null,
                FieldName: StatusFieldName,
                RawValue: null,
                ErrorCode: TelegramMessageParseErrorCode.MissingRequiredField,
                Message: "Required field 'Status' is missing."));
        }
        else if (string.IsNullOrWhiteSpace(statusEntry.RawValue))
        {
            errors.Add(new TelegramMessageParseError(
                LineNumber: statusEntry.LineNumber,
                FieldName: StatusFieldName,
                RawValue: statusEntry.RawValue,
                ErrorCode: TelegramMessageParseErrorCode.UnsupportedStatus,
                Message: $"Status on line {statusEntry.LineNumber} cannot be empty. Allowed values are 'Pending' or 'Completed'."));
        }
        else if (string.Equals(statusEntry.RawValue, nameof(BranchActivityStatus.Pending), StringComparison.OrdinalIgnoreCase))
        {
            parsedStatus = BranchActivityStatus.Pending;
        }
        else if (string.Equals(statusEntry.RawValue, nameof(BranchActivityStatus.Completed), StringComparison.OrdinalIgnoreCase))
        {
            parsedStatus = BranchActivityStatus.Completed;
        }
        else
        {
            errors.Add(new TelegramMessageParseError(
                LineNumber: statusEntry.LineNumber,
                FieldName: StatusFieldName,
                RawValue: statusEntry.RawValue,
                ErrorCode: TelegramMessageParseErrorCode.UnsupportedStatus,
                Message: $"Status '{statusEntry.RawValue}' on line {statusEntry.LineNumber} is not supported. Only 'Pending' and 'Completed' are allowed."));
        }

        if (errors.Count > 0 ||
            normalizedActivityCode is null ||
            normalizedBranchCode is null ||
            !parsedStatus.HasValue)
        {
            return Failure(errors);
        }

        var parsedMessage = new ParsedTelegramActivityMessage(
            ActivityCode: normalizedActivityCode,
            BranchCode: normalizedBranchCode,
            Status: parsedStatus.Value,
            RawActivityValue: activityEntry.RawValue,
            RawBranchValue: branchEntry.RawValue,
            RawStatusValue: statusEntry.RawValue);

        return new TelegramMessageParseResult(
            IsSuccess: true,
            ParsedMessage: parsedMessage,
            PrimaryFailureReason: null,
            Errors: Array.Empty<TelegramMessageParseError>());
    }

    private static string? ResolveCanonicalFieldName(string rawKey)
    {
        var normalizedKey = WhitespaceAndUnderscoreRegex().Replace(rawKey, string.Empty).ToUpperInvariant();
        return normalizedKey switch
        {
            "ACTIVITY" or "ACTIVITYCODE" => ActivityFieldName,
            "BRANCHID" or "BRANCHCODE" or "BRANCH" => BranchIdFieldName,
            "STATUS" => StatusFieldName,
            _ => null
        };
    }

    private static TelegramMessageParseResult Failure(IReadOnlyList<TelegramMessageParseError> errors)
    {
        var primaryReason = string.Join(" ", errors.Select(e => e.Message));
        return new TelegramMessageParseResult(
            IsSuccess: false,
            ParsedMessage: null,
            PrimaryFailureReason: primaryReason,
            Errors: errors);
    }
}
