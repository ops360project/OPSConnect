using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Telegram;

namespace OpsConnect.Application.Activities.Common;

/// <summary>
/// Base class for pluggable operational activity handlers (e.g., RFFO, GetData).
/// Enforces consistent ActivityCode normalization, CanHandle matching, and command validation.
/// </summary>
public abstract class ActivityUpdateHandlerBase : IActivityUpdateHandler
{
    protected ActivityUpdateHandlerBase(string activityCode)
    {
        ActivityCode = ActivityMaster.ValidateActivityCode(activityCode);
    }

    public string ActivityCode { get; }

    public virtual bool CanHandle(string? activityCode)
    {
        if (string.IsNullOrWhiteSpace(activityCode))
        {
            return false;
        }

        return string.Equals(ActivityCode, activityCode.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public Task<ActivityUpdateProcessingResult> ProcessUpdateAsync(
        ActivityUpdateSubmissionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var normalizedCommand = command.ValidateAndNormalize();
        if (!CanHandle(normalizedCommand.ActivityCode))
        {
            throw new InvalidOperationException(
                $"Handler for activity '{ActivityCode}' cannot process command for activity '{normalizedCommand.ActivityCode}'.");
        }

        return HandleValidatedUpdateAsync(normalizedCommand, cancellationToken);
    }

    protected abstract Task<ActivityUpdateProcessingResult> HandleValidatedUpdateAsync(
        ActivityUpdateSubmissionCommand command,
        CancellationToken cancellationToken);
}

/// <summary>
/// Application-layer coordinator that parses raw Telegram activity messages and dispatches
/// validated commands to the registered IActivityUpdateHandler for the target ActivityCode.
/// </summary>
public sealed class ActivityUpdateProcessor : IActivityUpdateProcessor
{
    private readonly ITelegramActivityMessageParser _messageParser;
    private readonly Dictionary<string, IActivityUpdateHandler> _handlersByCode;
    private readonly IReadOnlyCollection<string> _registeredCodes;

    public ActivityUpdateProcessor(
        ITelegramActivityMessageParser messageParser,
        IEnumerable<IActivityUpdateHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(messageParser);
        ArgumentNullException.ThrowIfNull(handlers);

        _messageParser = messageParser;
        _handlersByCode = new Dictionary<string, IActivityUpdateHandler>(StringComparer.OrdinalIgnoreCase);

        foreach (var handler in handlers)
        {
            if (handler is null)
            {
                continue;
            }

            var normalizedCode = ActivityMaster.ValidateActivityCode(handler.ActivityCode);
            if (!_handlersByCode.TryAdd(normalizedCode, handler))
            {
                throw new InvalidOperationException(
                    $"Duplicate IActivityUpdateHandler registration detected for ActivityCode '{normalizedCode}'.");
            }
        }

        _registeredCodes = _handlersByCode.Keys
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyCollection<string> RegisteredActivityCodes => _registeredCodes;

    public IActivityUpdateHandler? ResolveHandler(string? activityCode)
    {
        if (string.IsNullOrWhiteSpace(activityCode))
        {
            return null;
        }

        var trimmed = activityCode.Trim();
        if (_handlersByCode.TryGetValue(trimmed, out var handler))
        {
            return handler;
        }

        return _handlersByCode.Values.FirstOrDefault(h => h.CanHandle(trimmed));
    }

    public async Task<ActivityUpdateProcessingResult> ProcessRawMessageAsync(
        RawActivityMessageSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? normalizedExpectedActivityCode = null;
        if (!string.IsNullOrWhiteSpace(request.ExpectedActivityCode))
        {
            try
            {
                normalizedExpectedActivityCode = ActivityMaster.ValidateActivityCode(request.ExpectedActivityCode);
            }
            catch (ArgumentException ex)
            {
                return new ActivityUpdateProcessingResult(
                    IsAccepted: false,
                    ProcessingStatus: MessageProcessingStatus.RejectedInvalidFormat,
                    Outcome: TransitionOutcome.Rejected,
                    ActivityCode: null,
                    ActivityId: null,
                    BranchCode: null,
                    BranchId: null,
                    ReportingDate: request.ExplicitReportingDate,
                    PreviousStatus: null,
                    CurrentStatus: null,
                    DidStatusChange: false,
                    DidConsumeReopenPermission: false,
                    ConsumedPermissionId: null,
                    DidReactivateReportingCycle: false,
                    DidTriggerImmediateFinalReportCondition: false,
                    RejectionReason: ex.Message,
                    ReplyMessage: $"Update rejected (Invalid Activity Code): {ex.Message}");
            }
        }

        var parseResult = _messageParser.Parse(request.RawMessageText);
        if (!parseResult.IsSuccess || parseResult.ParsedMessage is null)
        {
            var reason = parseResult.PrimaryFailureReason ?? "Message format is invalid.";
            return new ActivityUpdateProcessingResult(
                IsAccepted: false,
                ProcessingStatus: MessageProcessingStatus.RejectedInvalidFormat,
                Outcome: TransitionOutcome.Rejected,
                ActivityCode: normalizedExpectedActivityCode,
                ActivityId: null,
                BranchCode: null,
                BranchId: null,
                ReportingDate: request.ExplicitReportingDate,
                PreviousStatus: null,
                CurrentStatus: null,
                DidStatusChange: false,
                DidConsumeReopenPermission: false,
                ConsumedPermissionId: null,
                DidReactivateReportingCycle: false,
                DidTriggerImmediateFinalReportCondition: false,
                RejectionReason: reason,
                ReplyMessage: $"Update rejected (Invalid Format): {reason}",
                ParseErrors: parseResult.Errors);
        }

        var parsed = parseResult.ParsedMessage;
        if (normalizedExpectedActivityCode is not null &&
            !string.Equals(normalizedExpectedActivityCode, parsed.ActivityCode, StringComparison.OrdinalIgnoreCase))
        {
            var mismatchReason =
                $"Submitted activity code '{normalizedExpectedActivityCode}' does not match message payload activity '{parsed.ActivityCode}'.";
            return new ActivityUpdateProcessingResult(
                IsAccepted: false,
                ProcessingStatus: MessageProcessingStatus.RejectedInvalidFormat,
                Outcome: TransitionOutcome.Rejected,
                ActivityCode: parsed.ActivityCode,
                ActivityId: null,
                BranchCode: parsed.BranchCode,
                BranchId: null,
                ReportingDate: request.ExplicitReportingDate,
                PreviousStatus: null,
                CurrentStatus: null,
                DidStatusChange: false,
                DidConsumeReopenPermission: false,
                ConsumedPermissionId: null,
                DidReactivateReportingCycle: false,
                DidTriggerImmediateFinalReportCondition: false,
                RejectionReason: mismatchReason,
                ReplyMessage: $"Update rejected (Activity Mismatch): {mismatchReason}");
        }

        var command = new ActivityUpdateSubmissionCommand(
            ActivityCode: parsed.ActivityCode,
            BranchCode: parsed.BranchCode,
            IncomingStatus: parsed.Status,
            TelegramMessageLogId: request.TelegramMessageLogId,
            TelegramChatId: request.TelegramChatId,
            TelegramMessageId: request.TelegramMessageId,
            TelegramUserId: request.TelegramUserId,
            SenderUsername: request.SenderUsername,
            SenderDisplayName: request.SenderDisplayName,
            OccurredAtUtc: request.OccurredAtUtc,
            ExplicitReportingDate: request.ExplicitReportingDate);

        return await ProcessParsedUpdateAsync(command, cancellationToken);
    }

    public async Task<ActivityUpdateProcessingResult> ProcessParsedUpdateAsync(
        ActivityUpdateSubmissionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var normalizedCommand = command.ValidateAndNormalize();
        var handler = ResolveHandler(normalizedCommand.ActivityCode);

        if (handler is null)
        {
            var reason = $"Activity '{normalizedCommand.ActivityCode}' is not supported or has no registered handler.";
            return new ActivityUpdateProcessingResult(
                IsAccepted: false,
                ProcessingStatus: MessageProcessingStatus.RejectedUnknownActivity,
                Outcome: TransitionOutcome.Rejected,
                ActivityCode: normalizedCommand.ActivityCode,
                ActivityId: null,
                BranchCode: normalizedCommand.BranchCode,
                BranchId: null,
                ReportingDate: normalizedCommand.ExplicitReportingDate,
                PreviousStatus: null,
                CurrentStatus: null,
                DidStatusChange: false,
                DidConsumeReopenPermission: false,
                ConsumedPermissionId: null,
                DidReactivateReportingCycle: false,
                DidTriggerImmediateFinalReportCondition: false,
                RejectionReason: reason,
                ReplyMessage: $"Update rejected: {reason}");
        }

        return await handler.ProcessUpdateAsync(normalizedCommand, cancellationToken);
    }
}
