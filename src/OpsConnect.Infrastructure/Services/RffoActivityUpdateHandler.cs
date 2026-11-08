using Microsoft.EntityFrameworkCore;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Activities.RFFO;
using OpsConnect.Application.Common;
using OpsConnect.Application.Reporting;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Reporting;
using OpsConnect.Domain.Telegram;
using OpsConnect.Infrastructure.Persistence;

namespace OpsConnect.Infrastructure.Services;

/// <summary>
/// Transactional RFFO (Request for Field Force Operations) activity status handler.
/// Enforces active branch/hierarchy validation, Asia/Kolkata business reporting date resolution,
/// RFFO status transition rules, single-use administrator reopening consumption, reporting cycle reactivation,
/// duplicate source message suppression, and atomic status + history persistence.
/// </summary>
public sealed class RffoActivityUpdateHandler : ActivityUpdateHandlerBase, IRffoActivityUpdateHandler
{
    private readonly OpsConnectDbContext _dbContext;
    private readonly IBusinessClock _businessClock;
    private readonly IScheduledReportingService? _scheduledReportingService;
    private readonly IActivityReportingService? _reportingService;

    public RffoActivityUpdateHandler(
        OpsConnectDbContext dbContext,
        IBusinessClock businessClock,
        IScheduledReportingService? scheduledReportingService = null,
        IActivityReportingService? reportingService = null)
        : base(RffoActivityDescriptor.ActivityCode)
    {
        _dbContext = dbContext;
        _businessClock = businessClock;
        _scheduledReportingService = scheduledReportingService;
        _reportingService = reportingService;
    }

    protected override async Task<ActivityUpdateProcessingResult> HandleValidatedUpdateAsync(
        ActivityUpdateSubmissionCommand command,
        CancellationToken cancellationToken)
    {
        var ambientTransaction = _dbContext.Database.CurrentTransaction;
        if (ambientTransaction is not null)
        {
            const string savepointName = "RffoUpdateSavepoint";
            await ambientTransaction.CreateSavepointAsync(savepointName, cancellationToken);
            try
            {
                var result = await ExecuteTransactionalUpdateAsync(command, cancellationToken);
                await ambientTransaction.ReleaseSavepointAsync(savepointName, cancellationToken);
                return result;
            }
            catch
            {
                await ambientTransaction.RollbackToSavepointAsync(savepointName, cancellationToken);
                _dbContext.ChangeTracker.Clear();
                throw;
            }
        }

        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var result = await ExecuteTransactionalUpdateAsync(command, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                _dbContext.ChangeTracker.Clear();
                throw;
            }
        });
    }

    private async Task<ActivityUpdateProcessingResult> ExecuteTransactionalUpdateAsync(
        ActivityUpdateSubmissionCommand command,
        CancellationToken cancellationToken)
    {
        var occurredAtUtc = command.OccurredAtUtc.HasValue
            ? (command.OccurredAtUtc.Value.Kind == DateTimeKind.Utc
                ? command.OccurredAtUtc.Value
                : DateTime.SpecifyKind(command.OccurredAtUtc.Value, DateTimeKind.Utc))
            : _businessClock.UtcNow;

        // 1. Validate ActivityMaster exists and is active
        var activity = await _dbContext.Activities
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.ActivityCode == command.ActivityCode, cancellationToken);

        if (activity is null || !activity.IsActive)
        {
            var reason = activity is null
                ? $"Activity '{command.ActivityCode}' does not exist."
                : $"Activity '{command.ActivityCode}' is currently inactive.";

            return new ActivityUpdateProcessingResult(
                IsAccepted: false,
                ProcessingStatus: MessageProcessingStatus.RejectedUnknownActivity,
                Outcome: TransitionOutcome.Rejected,
                ActivityCode: command.ActivityCode,
                ActivityId: activity?.ActivityId,
                BranchCode: command.BranchCode,
                BranchId: null,
                ReportingDate: command.ExplicitReportingDate,
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

        // 2. Prevent duplicate processing of the same source Telegram message
        if (command.TelegramMessageLogId.HasValue)
        {
            bool alreadyProcessed = await _dbContext.ActivityHistories
                .AsNoTracking()
                .AnyAsync(h => h.TelegramMessageLogId == command.TelegramMessageLogId.Value, cancellationToken);

            if (alreadyProcessed)
            {
                var dupReason = $"Source message ID {command.TelegramMessageLogId.Value} has already been processed.";
                return new ActivityUpdateProcessingResult(
                    IsAccepted: false,
                    ProcessingStatus: MessageProcessingStatus.DuplicateIgnored,
                    Outcome: TransitionOutcome.Rejected,
                    ActivityCode: activity.ActivityCode,
                    ActivityId: activity.ActivityId,
                    BranchCode: command.BranchCode,
                    BranchId: null,
                    ReportingDate: command.ExplicitReportingDate,
                    PreviousStatus: null,
                    CurrentStatus: null,
                    DidStatusChange: false,
                    DidConsumeReopenPermission: false,
                    ConsumedPermissionId: null,
                    DidReactivateReportingCycle: false,
                    DidTriggerImmediateFinalReportCondition: false,
                    RejectionReason: dupReason,
                    ReplyMessage: dupReason);
            }
        }

        // 3. Resolve activity reporting timezone and business ReportingDate (default Asia/Kolkata)
        var timeZoneId = await ResolveActivityTimeZoneAsync(activity.ActivityId, command.ExplicitReportingDate, cancellationToken);
        var reportingDate = command.ExplicitReportingDate
            ?? DateOnly.FromDateTime(_businessClock.ConvertUtcToTimeZone(occurredAtUtc, timeZoneId).DateTime);

        // 4. Validate BranchMaster exists, is active, and has an active hierarchy mapping on ReportingDate
        var branch = await _dbContext.Branches
            .Include(b => b.Zone)
            .Include(b => b.State)
            .Include(b => b.Region)
            .Include(b => b.MappingHistories)
            .SingleOrDefaultAsync(b => b.BranchCode == command.BranchCode, cancellationToken);

        if (branch is null)
        {
            var reason = $"Branch '{command.BranchCode}' is not registered in Organisation Master.";
            return new ActivityUpdateProcessingResult(
                IsAccepted: false,
                ProcessingStatus: MessageProcessingStatus.RejectedUnknownBranch,
                Outcome: TransitionOutcome.Rejected,
                ActivityCode: activity.ActivityCode,
                ActivityId: activity.ActivityId,
                BranchCode: command.BranchCode,
                BranchId: null,
                ReportingDate: reportingDate,
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

        if (!branch.CanSubmitActivityUpdate(out var inactiveReason))
        {
            return new ActivityUpdateProcessingResult(
                IsAccepted: false,
                ProcessingStatus: MessageProcessingStatus.RejectedInactiveBranch,
                Outcome: TransitionOutcome.Rejected,
                ActivityCode: activity.ActivityCode,
                ActivityId: activity.ActivityId,
                BranchCode: branch.BranchCode,
                BranchId: branch.BranchId,
                ReportingDate: reportingDate,
                PreviousStatus: null,
                CurrentStatus: null,
                DidStatusChange: false,
                DidConsumeReopenPermission: false,
                ConsumedPermissionId: null,
                DidReactivateReportingCycle: false,
                DidTriggerImmediateFinalReportCondition: false,
                RejectionReason: inactiveReason,
                ReplyMessage: $"Update rejected: {inactiveReason}");
        }

        var effectiveMapping = branch.ResolveMappingAsOf(reportingDate);
        if (effectiveMapping is null)
        {
            var unmappedReason = $"Branch '{branch.BranchCode}' does not have an effective organisation hierarchy mapping for reporting date {reportingDate:yyyy-MM-dd}.";
            return new ActivityUpdateProcessingResult(
                IsAccepted: false,
                ProcessingStatus: MessageProcessingStatus.RejectedInactiveBranch,
                Outcome: TransitionOutcome.Rejected,
                ActivityCode: activity.ActivityCode,
                ActivityId: activity.ActivityId,
                BranchCode: branch.BranchCode,
                BranchId: branch.BranchId,
                ReportingDate: reportingDate,
                PreviousStatus: null,
                CurrentStatus: null,
                DidStatusChange: false,
                DidConsumeReopenPermission: false,
                ConsumedPermissionId: null,
                DidReactivateReportingCycle: false,
                DidTriggerImmediateFinalReportCondition: false,
                RejectionReason: unmappedReason,
                ReplyMessage: $"Update rejected: {unmappedReason}");
        }

        // 5. Expire any stale Available permissions from prior reporting dates so they never carry over
        var stalePermissions = await _dbContext.StatusReopenPermissions
            .Where(p => p.BranchId == branch.BranchId &&
                        p.ActivityId == activity.ActivityId &&
                        p.PermissionStatus == ReopenPermissionStatus.Available &&
                        p.ReportingDate < reportingDate)
            .ToListAsync(cancellationToken);

        if (stalePermissions.Count > 0)
        {
            StatusReopenPermission.ExpireStalePermissions(stalePermissions, reportingDate);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        // 6. Load existing ActivityStatus for today or initialise logically at NotReported
        var statusEntity = await _dbContext.ActivityStatuses
            .SingleOrDefaultAsync(
                s => s.ActivityId == activity.ActivityId &&
                     s.BranchId == branch.BranchId &&
                     s.ReportingDate == reportingDate,
                cancellationToken);

        bool isNewStatusRow = statusEntity is null;
        var actorName = command.SenderDisplayName ?? command.SenderUsername ?? "Telegram";

        statusEntity ??= new ActivityStatus
        {
            ActivityId = activity.ActivityId,
            BranchId = branch.BranchId,
            ReportingDate = reportingDate,
            CurrentStatus = BranchActivityStatus.NotReported,
            CreatedAtUtc = occurredAtUtc,
            CreatedBy = actorName
        };

        // 7. Load same-day Available reopening permission if transitioning Completed -> Pending
        StatusReopenPermission? reopenPermission = null;
        if (statusEntity.CurrentStatus == BranchActivityStatus.Completed &&
            command.IncomingStatus == BranchActivityStatus.Pending)
        {
            reopenPermission = await _dbContext.StatusReopenPermissions
                .SingleOrDefaultAsync(
                    p => p.BranchId == branch.BranchId &&
                         p.ActivityId == activity.ActivityId &&
                         p.ReportingDate == reportingDate &&
                         p.PermissionStatus == ReopenPermissionStatus.Available,
                    cancellationToken);
        }

        // 8. Evaluate transition using domain rules
        var transition = statusEntity.ApplyTransition(
            incomingStatus: command.IncomingStatus,
            currentReportingDate: reportingDate,
            telegramMessageLogId: command.TelegramMessageLogId ?? 0,
            telegramUserId: command.TelegramUserId,
            senderDisplayName: command.SenderDisplayName ?? command.SenderUsername,
            occurredAtUtc: occurredAtUtc,
            reopenPermission: reopenPermission);

        if (transition.IsAccepted && transition.DidStatusChange)
        {
            statusEntity.UpdatedBy = actorName;
            if (isNewStatusRow)
            {
                _dbContext.ActivityStatuses.Add(statusEntity);
            }
        }

        // 9. Reactivate completed ReportingCycle if a reopening permission was consumed
        bool didReactivateCycle = false;
        if (transition.DidConsumeReopenPermission && reopenPermission is not null)
        {
            var cycle = await _dbContext.ReportingCycles
                .Include(c => c.Slots)
                .SingleOrDefaultAsync(
                    c => c.ActivityId == activity.ActivityId &&
                         c.ReportingDate == reportingDate,
                    cancellationToken);

            if (cycle is not null && cycle.CycleStatus == ReportingCycleStatus.Completed)
            {
                didReactivateCycle = cycle.ReactivateAfterAuthorisedReopen(reopenPermission, occurredAtUtc);
            }
        }

        // 10. Always record immutable ActivityHistory audit entry (including Accepted, AcceptedUnchanged, AcceptedViaReopen, and Rejected)
        var historyEntry = new ActivityHistory
        {
            ActivityId = activity.ActivityId,
            BranchId = branch.BranchId,
            ReportingDate = reportingDate,
            PreviousStatus = transition.PreviousStatus,
            NewStatus = transition.NewStatus,
            Outcome = transition.Outcome,
            TelegramMessageLogId = command.TelegramMessageLogId,
            ConsumedReopenPermissionId = transition.ConsumedPermissionId,
            TelegramUserId = command.TelegramUserId,
            TelegramUsername = command.SenderUsername,
            TelegramDisplayName = command.SenderDisplayName,
            Notes = BuildHistoryNotes(command, transition),
            RecordedAtUtc = occurredAtUtc
        };

        _dbContext.ActivityHistories.Add(historyEntry);

        // 11. Persist status, permission consumption, cycle reactivation, and history atomically
        await _dbContext.SaveChangesAsync(cancellationToken);

        // 12. Evaluate whether all active mapped branches have now reached Completed
        bool didTriggerFinalCondition = false;
        if (transition.IsAccepted &&
            transition.DidStatusChange &&
            transition.NewStatus == BranchActivityStatus.Completed)
        {
            if (_scheduledReportingService is not null)
            {
                var finalResult = await _scheduledReportingService.ExecuteImmediateFinalReportAsync(
                    activity.ActivityCode,
                    reportingDate,
                    cancellationToken);

                didTriggerFinalCondition = finalResult.IsTriggered;
            }
            else
            {
                var reportingSvc = _reportingService ?? new ActivityReportingService(_dbContext, _businessClock);
                var summary = await reportingSvc.GetActivitySummaryReportAsync(
                    new ReportingFilterQuery
                    {
                        ActivityCode = activity.ActivityCode,
                        ReportingDate = reportingDate
                    },
                    cancellationToken);

                didTriggerFinalCondition = summary.TotalBranches > 0 &&
                                           summary.CompletedBranches == summary.TotalBranches &&
                                           summary.SubmittedPendingBranches == 0 &&
                                           summary.NotReportedBranches == 0;
            }
        }

        var processingStatus = transition.Outcome switch
        {
            TransitionOutcome.Accepted or TransitionOutcome.AcceptedViaReopen => MessageProcessingStatus.Accepted,
            TransitionOutcome.AcceptedUnchanged => MessageProcessingStatus.AcceptedUnchanged,
            _ => MessageProcessingStatus.RejectedInvalidTransition
        };

        var replyMessage = BuildReplyMessage(activity.ActivityCode, branch.BranchCode, reportingDate, transition);

        return new ActivityUpdateProcessingResult(
            IsAccepted: transition.IsAccepted,
            ProcessingStatus: processingStatus,
            Outcome: transition.Outcome,
            ActivityCode: activity.ActivityCode,
            ActivityId: activity.ActivityId,
            BranchCode: branch.BranchCode,
            BranchId: branch.BranchId,
            ReportingDate: reportingDate,
            PreviousStatus: transition.PreviousStatus,
            CurrentStatus: transition.NewStatus,
            DidStatusChange: transition.DidStatusChange,
            DidConsumeReopenPermission: transition.DidConsumeReopenPermission,
            ConsumedPermissionId: transition.ConsumedPermissionId,
            DidReactivateReportingCycle: didReactivateCycle,
            DidTriggerImmediateFinalReportCondition: didTriggerFinalCondition,
            RejectionReason: transition.RejectionReason,
            ReplyMessage: replyMessage);
    }

    private async Task<string> ResolveActivityTimeZoneAsync(
        int activityId,
        DateOnly? explicitDate,
        CancellationToken cancellationToken)
    {
        var configs = await _dbContext.ActivityReportingConfigurations
            .AsNoTracking()
            .Where(c => c.ActivityId == activityId)
            .OrderByDescending(c => c.EffectiveFrom)
            .ToListAsync(cancellationToken);

        if (configs.Count == 0)
        {
            return ActivityReportingConfiguration.DefaultTimeZoneId;
        }

        if (explicitDate.HasValue)
        {
            var matching = configs.FirstOrDefault(c => c.IsEffectiveOn(explicitDate.Value));
            if (matching is not null)
            {
                return matching.TimeZoneId;
            }
        }

        var openConfig = configs.FirstOrDefault(c => c.EffectiveTo == null) ?? configs[0];
        return openConfig.TimeZoneId;
    }

    private static string BuildHistoryNotes(
        ActivityUpdateSubmissionCommand command,
        StatusTransitionResult transition)
    {
        if (!transition.IsAccepted)
        {
            return transition.RejectionReason ?? $"Rejected transition from {transition.PreviousStatus} to {command.IncomingStatus}.";
        }

        return transition.Outcome switch
        {
            TransitionOutcome.AcceptedUnchanged =>
                $"Unchanged status submission ({transition.CurrentStatusForNotes()}); current status retained.",
            TransitionOutcome.AcceptedViaReopen =>
                $"Status reopened from {transition.PreviousStatus} to {transition.NewStatus} using permission ID {transition.ConsumedPermissionId}.",
            _ =>
                $"Status transitioned from {transition.PreviousStatus} to {transition.NewStatus}."
        };
    }

    private static string BuildReplyMessage(
        string activityCode,
        string branchCode,
        DateOnly reportingDate,
        StatusTransitionResult transition)
    {
        if (!transition.IsAccepted)
        {
            return $"[{activityCode}] Branch {branchCode} ({reportingDate:dd-MMM-yyyy}): Update rejected. {transition.RejectionReason}";
        }

        return transition.Outcome switch
        {
            TransitionOutcome.AcceptedUnchanged =>
                $"[{activityCode}] Branch {branchCode} ({reportingDate:dd-MMM-yyyy}): Status is already {transition.NewStatus} (no change).",
            TransitionOutcome.AcceptedViaReopen =>
                $"[{activityCode}] Branch {branchCode} ({reportingDate:dd-MMM-yyyy}): Status reopened from {transition.PreviousStatus} to {transition.NewStatus}.",
            _ =>
                $"[{activityCode}] Branch {branchCode} ({reportingDate:dd-MMM-yyyy}): Status updated from {transition.PreviousStatus} to {transition.NewStatus}."
        };
    }
}

internal static class StatusTransitionResultExtensions
{
    public static BranchActivityStatus CurrentStatusForNotes(this StatusTransitionResult result) => result.NewStatus;
}
