using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsConnect.Application.Common;
using OpsConnect.Application.Reporting;
using OpsConnect.Application.Telegram;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Reporting;
using OpsConnect.Domain.Telegram;
using OpsConnect.Infrastructure.Persistence;
using OpsConnect.Infrastructure.Telegram;

namespace OpsConnect.Infrastructure.Services;

/// <summary>
/// Manages durable scheduled reporting slots, generates immutable report snapshots,
/// and delivers scheduled Telegram reports to configured active destination groups.
/// </summary>
public sealed class ScheduledReportingService : IScheduledReportingService
{
    private static readonly TimeSpan DefaultCatchUpWindow = TimeSpan.FromHours(4);
    private static readonly TimeSpan StaleProcessingThreshold = TimeSpan.FromMinutes(5);

    private readonly OpsConnectDbContext _dbContext;
    private readonly IActivityReportingService _reportingService;
    private readonly ITelegramReportFormatter _reportFormatter;
    private readonly ITelegramBotClient _telegramBotClient;
    private readonly IBusinessClock _businessClock;
    private readonly IOptionsMonitor<TelegramBotOptions> _botOptionsMonitor;
    private readonly ILogger<ScheduledReportingService> _logger;

    public ScheduledReportingService(
        OpsConnectDbContext dbContext,
        IActivityReportingService reportingService,
        ITelegramReportFormatter reportFormatter,
        ITelegramBotClient telegramBotClient,
        IBusinessClock businessClock,
        IOptionsMonitor<TelegramBotOptions> botOptionsMonitor,
        ILogger<ScheduledReportingService> logger)
    {
        _dbContext = dbContext;
        _reportingService = reportingService;
        _reportFormatter = reportFormatter;
        _telegramBotClient = telegramBotClient;
        _businessClock = businessClock;
        _botOptionsMonitor = botOptionsMonitor;
        _logger = logger;
    }

    public async Task<ScheduledReportingDispatchResult> DispatchDueReportsAsync(
        DateTime? asOfUtc = null,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = asOfUtc ?? _businessClock.UtcNow;

        var activeActivities = await _dbContext.Activities
            .AsNoTracking()
            .Where(a => a.IsActive)
            .ToListAsync(cancellationToken);

        int cyclesEnsured = 0;
        int slotsEvaluated = 0;
        int slotsClaimedAndProcessed = 0;
        int slotsSkipped = 0;
        int slotsFailed = 0;
        var executedResults = new List<SlotExecutionResult>();

        foreach (var activity in activeActivities)
        {
            // 1. Resolve configuration and effective business date for this activity
            var configs = await _dbContext.ActivityReportingConfigurations
                .AsNoTracking()
                .Where(c => c.ActivityId == activity.ActivityId)
                .OrderByDescending(c => c.EffectiveFrom)
                .ToListAsync(cancellationToken);

            if (configs.Count == 0)
            {
                continue;
            }

            var openConfig = configs.FirstOrDefault(c => c.EffectiveTo == null) ?? configs[0];
            var reportingDate = _businessClock.GetBusinessDate(openConfig.TimeZoneId);

            var effectiveConfig = configs.FirstOrDefault(c => c.IsEffectiveOn(reportingDate));
            if (effectiveConfig is null || !effectiveConfig.IsReportingEnabled)
            {
                // A disabled activity or disabled schedule must not generate new reports.
                continue;
            }

            // 2. Ensure durable ReportingCycle and daily ReportingSlot instances exist
            var cycle = await EnsureDailyReportingCycleAndSlotsAsync(
                activity,
                effectiveConfig,
                reportingDate,
                nowUtc,
                cancellationToken);

            if (cycle is null)
            {
                continue;
            }

            cyclesEnsured++;

            // 3. Find due slots
            var candidateSlots = await _dbContext.ReportingSlots
                .Where(s => s.ReportingCycleId == cycle.ReportingCycleId &&
                            s.ScheduledExecutionUtc <= nowUtc &&
                            (s.SlotStatus == ReportDeliveryStatus.Pending ||
                             s.SlotStatus == ReportDeliveryStatus.DeliveryPending ||
                             (s.SlotStatus == ReportDeliveryStatus.Processing && s.UpdatedAtUtc < nowUtc - StaleProcessingThreshold)))
                .OrderBy(s => s.ScheduledSlotTime)
                .ToListAsync(cancellationToken);

            foreach (var slot in candidateSlots)
            {
                slotsEvaluated++;

                // Catch-up policy: Expire slots older than safe catch-up window
                if (slot.ScheduledExecutionUtc < nowUtc - DefaultCatchUpWindow)
                {
                    slot.SlotStatus = ReportDeliveryStatus.Failed;
                    slot.StatusNotes = $"Expired: Scheduled at {slot.ScheduledExecutionUtc:u}, exceeding safe catch-up window of {DefaultCatchUpWindow.TotalHours} hours.";
                    slot.UpdatedAtUtc = nowUtc;
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    slotsSkipped++;
                    continue;
                }

                // Concurrency-safe claim
                bool claimed = await TryClaimSlotForProcessingAsync(slot, nowUtc, cancellationToken);
                if (!claimed)
                {
                    slotsSkipped++;
                    continue;
                }

                // Execute claimed slot
                var executionResult = await ExecuteSlotInternalAsync(slot, activity, effectiveConfig, nowUtc, cancellationToken);
                executedResults.Add(executionResult);

                if (executionResult.Status == ReportDeliveryStatus.Delivered)
                {
                    slotsClaimedAndProcessed++;
                }
                else
                {
                    slotsFailed++;
                }
            }
        }

        return new ScheduledReportingDispatchResult(
            DispatchTimestampUtc: nowUtc,
            ActivitiesEvaluated: activeActivities.Count,
            CyclesEnsured: cyclesEnsured,
            SlotsEvaluated: slotsEvaluated,
            SlotsClaimedAndProcessed: slotsClaimedAndProcessed,
            SlotsSkipped: slotsSkipped,
            SlotsFailed: slotsFailed,
            ExecutedSlots: executedResults);
    }

    public async Task<SlotExecutionResult> ExecuteSlotAsync(
        long reportingSlotId,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = _businessClock.UtcNow;

        var slot = await _dbContext.ReportingSlots
            .Include(s => s.ReportingCycle)
            .SingleOrDefaultAsync(s => s.ReportingSlotId == reportingSlotId, cancellationToken)
            ?? throw new KeyNotFoundException($"ReportingSlot ID {reportingSlotId} was not found.");

        var activity = await _dbContext.Activities
            .SingleOrDefaultAsync(a => a.ActivityId == slot.ActivityId, cancellationToken)
            ?? throw new KeyNotFoundException($"Activity ID {slot.ActivityId} was not found.");

        if (!activity.IsActive)
        {
            slot.SlotStatus = ReportDeliveryStatus.CancelledByConfiguration;
            slot.StatusNotes = $"Activity '{activity.ActivityCode}' is inactive.";
            slot.UpdatedAtUtc = nowUtc;
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new SlotExecutionResult(
                ReportingSlotId: slot.ReportingSlotId,
                ActivityId: slot.ActivityId,
                ActivityCode: activity.ActivityCode,
                ReportingDate: slot.ReportingDate,
                ScheduledSlotTime: slot.ScheduledSlotTime,
                Status: slot.SlotStatus,
                TotalBranches: 0,
                CompletedBranches: 0,
                CompletionPercentage: 0.00m,
                DestinationGroupsTargeted: 0,
                DestinationGroupsDelivered: 0,
                MessagePartsSent: 0,
                IsIdempotentRetry: false,
                StatusNotes: slot.StatusNotes,
                ErrorMessage: slot.StatusNotes);
        }

        var configs = await _dbContext.ActivityReportingConfigurations
            .AsNoTracking()
            .Where(c => c.ActivityId == slot.ActivityId)
            .OrderByDescending(c => c.EffectiveFrom)
            .ToListAsync(cancellationToken);

        var effectiveConfig = configs.FirstOrDefault(c => c.IsEffectiveOn(slot.ReportingDate));

        if (effectiveConfig is null || !effectiveConfig.IsReportingEnabled)
        {
            slot.SlotStatus = ReportDeliveryStatus.CancelledByConfiguration;
            slot.StatusNotes = "Reporting configuration is disabled or not effective for this date.";
            slot.UpdatedAtUtc = nowUtc;
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new SlotExecutionResult(
                ReportingSlotId: slot.ReportingSlotId,
                ActivityId: slot.ActivityId,
                ActivityCode: activity.ActivityCode,
                ReportingDate: slot.ReportingDate,
                ScheduledSlotTime: slot.ScheduledSlotTime,
                Status: slot.SlotStatus,
                TotalBranches: 0,
                CompletedBranches: 0,
                CompletionPercentage: 0.00m,
                DestinationGroupsTargeted: 0,
                DestinationGroupsDelivered: 0,
                MessagePartsSent: 0,
                IsIdempotentRetry: false,
                StatusNotes: slot.StatusNotes,
                ErrorMessage: slot.StatusNotes);
        }

        return await ExecuteSlotInternalAsync(slot, activity, effectiveConfig, nowUtc, cancellationToken);
    }

    public async Task<ScheduledReportingCycleStatusDto?> GetReportingCycleStatusAsync(
        string activityCode,
        DateOnly? reportingDate = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedCode = ActivityMaster.ValidateActivityCode(activityCode);

        var activity = await _dbContext.Activities
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.ActivityCode == normalizedCode, cancellationToken);

        if (activity is null)
        {
            return null;
        }

        var targetDate = reportingDate ?? _businessClock.GetBusinessDate(ActivityReportingConfiguration.DefaultTimeZoneId);

        var cycle = await _dbContext.ReportingCycles
            .AsNoTracking()
            .Include(c => c.Slots)
            .SingleOrDefaultAsync(c => c.ActivityId == activity.ActivityId && c.ReportingDate == targetDate, cancellationToken);

        if (cycle is null)
        {
            return null;
        }

        return new ScheduledReportingCycleStatusDto(
            ReportingCycleId: cycle.ReportingCycleId,
            ActivityId: cycle.ActivityId,
            ActivityCode: activity.ActivityCode,
            ReportingDate: cycle.ReportingDate,
            CycleStatus: cycle.CycleStatus,
            StopReason: cycle.StopReason,
            TotalSlots: cycle.Slots.Count,
            PendingSlots: cycle.Slots.Count(s => s.SlotStatus == ReportDeliveryStatus.Pending),
            ProcessingSlots: cycle.Slots.Count(s => s.SlotStatus == ReportDeliveryStatus.Processing),
            DeliveredSlots: cycle.Slots.Count(s => s.SlotStatus == ReportDeliveryStatus.Delivered),
            FailedSlots: cycle.Slots.Count(s => s.SlotStatus == ReportDeliveryStatus.Failed),
            SkippedSlots: cycle.Slots.Count(s => s.SlotStatus is ReportDeliveryStatus.SkippedAllCompleted or ReportDeliveryStatus.CancelledByConfiguration),
            CompletedAtUtc: cycle.CompletedAtUtc);
    }

    public async Task<IReadOnlyList<ScheduledSlotStatusDto>> GetScheduledSlotsAsync(
        string activityCode,
        DateOnly? reportingDate = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedCode = ActivityMaster.ValidateActivityCode(activityCode);

        var activity = await _dbContext.Activities
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.ActivityCode == normalizedCode, cancellationToken);

        if (activity is null)
        {
            return Array.Empty<ScheduledSlotStatusDto>();
        }

        var targetDate = reportingDate ?? _businessClock.GetBusinessDate(ActivityReportingConfiguration.DefaultTimeZoneId);

        var slots = await _dbContext.ReportingSlots
            .AsNoTracking()
            .Where(s => s.ActivityId == activity.ActivityId && s.ReportingDate == targetDate)
            .OrderBy(s => s.ScheduledSlotTime)
            .ToListAsync(cancellationToken);

        if (slots.Count == 0)
        {
            return Array.Empty<ScheduledSlotStatusDto>();
        }

        var slotIds = slots.Select(s => s.ReportingSlotId).ToList();
        var summaries = await _dbContext.SummaryLogs
            .AsNoTracking()
            .Include(s => s.Deliveries)
            .Where(s => s.ActivityId == activity.ActivityId && s.ReportingDate == targetDate)
            .ToListAsync(cancellationToken);

        var summariesBySlotTime = summaries
            .Where(s => s.ScheduledSlotTime.HasValue)
            .ToDictionary(s => s.ScheduledSlotTime!.Value);

        var result = new List<ScheduledSlotStatusDto>(slots.Count);

        foreach (var slot in slots)
        {
            summariesBySlotTime.TryGetValue(slot.ScheduledSlotTime, out var summary);

            var deliveries = summary?.Deliveries?
                .Select(d => new ScheduledDeliveryStatusDto(
                    ReportDeliveryId: d.ReportDeliveryId,
                    DestinationGroupId: d.DestinationGroupId,
                    DestinationTelegramChatId: d.DestinationTelegramChatId,
                    DeliveryStatus: d.DeliveryStatus,
                    DeliveryAttempts: d.DeliveryAttempts,
                    DeliveredTelegramMessageIds: d.DeliveredTelegramMessageIds,
                    DeliveredAtUtc: d.DeliveredAtUtc,
                    LastError: d.LastError))
                .ToList() ?? new List<ScheduledDeliveryStatusDto>();

            result.Add(new ScheduledSlotStatusDto(
                ReportingSlotId: slot.ReportingSlotId,
                ActivityId: slot.ActivityId,
                ActivityCode: activity.ActivityCode,
                ReportingDate: slot.ReportingDate,
                ScheduledSlotTime: slot.ScheduledSlotTime,
                ScheduledExecutionUtc: slot.ScheduledExecutionUtc,
                SlotStatus: slot.SlotStatus,
                ExecutedAtUtc: slot.ExecutedAtUtc,
                StatusNotes: slot.StatusNotes,
                ReportId: summary?.ReportId,
                Deliveries: deliveries));
        }

        return result;
    }

    private async Task<ReportingCycle?> EnsureDailyReportingCycleAndSlotsAsync(
        ActivityMaster activity,
        ActivityReportingConfiguration effectiveConfig,
        DateOnly reportingDate,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var cycle = await _dbContext.ReportingCycles
            .Include(c => c.Slots)
            .SingleOrDefaultAsync(
                c => c.ActivityId == activity.ActivityId && c.ReportingDate == reportingDate,
                cancellationToken);

        if (cycle is null)
        {
            cycle = new ReportingCycle
            {
                ActivityId = activity.ActivityId,
                ReportingDate = reportingDate,
                CycleStatus = ReportingCycleStatus.Active,
                StopReason = ReportingCycleStopReason.None,
                CreatedAtUtc = nowUtc,
                CreatedBy = "SystemScheduler"
            };

            _dbContext.ReportingCycles.Add(cycle);

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Transient race creating ReportingCycle for {ActivityCode} on {ReportingDate}.", activity.ActivityCode, reportingDate);
                _dbContext.Entry(cycle).State = EntityState.Detached;

                cycle = await _dbContext.ReportingCycles
                    .Include(c => c.Slots)
                    .SingleOrDefaultAsync(
                        c => c.ActivityId == activity.ActivityId && c.ReportingDate == reportingDate,
                        cancellationToken);

                if (cycle is null)
                {
                    return null;
                }
            }
        }

        // Generate missing slots for this configuration
        var slotInstances = effectiveConfig.CalculateDailySlotsWithUtc(reportingDate);
        var existingSlotTimes = cycle.Slots.Select(s => s.ScheduledSlotTime).ToHashSet();

        bool hasNewSlots = false;
        foreach (var slotInstance in slotInstances)
        {
            if (!existingSlotTimes.Contains(slotInstance.SlotTime))
            {
                var slot = new ReportingSlot
                {
                    ReportingCycleId = cycle.ReportingCycleId,
                    ActivityId = activity.ActivityId,
                    ReportingDate = reportingDate,
                    ScheduledSlotTime = slotInstance.SlotTime,
                    ScheduledExecutionUtc = slotInstance.ScheduledExecutionUtc,
                    SlotStatus = ReportDeliveryStatus.Pending,
                    CreatedAtUtc = nowUtc,
                    CreatedBy = "SystemScheduler"
                };

                _dbContext.ReportingSlots.Add(slot);
                cycle.Slots.Add(slot);
                existingSlotTimes.Add(slotInstance.SlotTime);
                hasNewSlots = true;
            }
        }

        if (hasNewSlots)
        {
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Transient race creating ReportingSlots for {ActivityCode} on {ReportingDate}.", activity.ActivityCode, reportingDate);
            }
        }

        return cycle;
    }

    private async Task<bool> TryClaimSlotForProcessingAsync(
        ReportingSlot slot,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            slot.SlotStatus = ReportDeliveryStatus.Processing;
            slot.ExecutedAtUtc = nowUtc;
            slot.UpdatedAtUtc = nowUtc;
            slot.StatusNotes = "Processing scheduled report delivery...";

            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogInformation(ex, "Slot {SlotId} was claimed concurrently by another worker.", slot.ReportingSlotId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error claiming slot {SlotId} for processing.", slot.ReportingSlotId);
            return false;
        }
    }

    private async Task<SlotExecutionResult> ExecuteSlotInternalAsync(
        ReportingSlot slot,
        ActivityMaster activity,
        ActivityReportingConfiguration effectiveConfig,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        string idempotencyKey = SummaryLog.BuildScheduledSlotIdempotencyKey(
            slot.ActivityId,
            slot.ReportingDate,
            slot.ScheduledSlotTime);

        // 1. Immutable report snapshot check:
        // A retry must not create a different report for the same slot simply because underlying branch statuses changed!
        var existingSummary = await _dbContext.SummaryLogs
            .Include(s => s.Deliveries)
            .SingleOrDefaultAsync(s => s.IdempotencyKey == idempotencyKey, cancellationToken);

        bool isIdempotentRetry = existingSummary is not null;
        IReadOnlyList<string> messageParts;
        int totalBranches;
        int completedBranches;
        decimal completionPercentage;

        if (existingSummary is null)
        {
            // Obtain report data and format immutable snapshot
            var summaryDto = await _reportingService.GetActivitySummaryReportAsync(
                new ReportingFilterQuery
                {
                    ActivityCode = activity.ActivityCode,
                    ReportingDate = slot.ReportingDate
                },
                cancellationToken);

            var branchDetails = await _reportingService.GetBranchReportDetailsAsync(
                new ReportingFilterQuery
                {
                    ActivityCode = activity.ActivityCode,
                    ReportingDate = slot.ReportingDate
                },
                cancellationToken);

            var formatted = _reportFormatter.FormatActivitySummary(
                summaryDto,
                branchDetails.Branches,
                new TelegramReportFormattingOptions
                {
                    TimeZoneId = effectiveConfig.TimeZoneId
                });

            messageParts = formatted.Messages;
            totalBranches = summaryDto.TotalBranches;
            completedBranches = summaryDto.CompletedBranches;
            completionPercentage = summaryDto.CompletionPercentage;

            existingSummary = new SummaryLog
            {
                ReportingCycleId = slot.ReportingCycleId,
                ActivityId = slot.ActivityId,
                ReportingDate = slot.ReportingDate,
                TriggerType = ReportingTriggerType.ScheduledSlot,
                ScheduledSlotTime = slot.ScheduledSlotTime,
                IdempotencyKey = idempotencyKey,
                TotalActiveBranches = summaryDto.TotalBranches,
                CompletedBranches = summaryDto.CompletedBranches,
                PendingBranches = summaryDto.SubmittedPendingBranches,
                NotReportedBranches = summaryDto.NotReportedBranches,
                CompletionPercentage = summaryDto.CompletionPercentage,
                FormattedReportContent = string.Join("\n\n---\n\n", messageParts),
                GeneratedAtUtc = nowUtc,
                TriggeredBy = "ScheduledReportingService"
            };

            _dbContext.SummaryLogs.Add(existingSummary);

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Duplicate SummaryLog creation for slot {SlotId}; re-reading existing snapshot.", slot.ReportingSlotId);
                _dbContext.Entry(existingSummary).State = EntityState.Detached;

                existingSummary = await _dbContext.SummaryLogs
                    .Include(s => s.Deliveries)
                    .SingleOrDefaultAsync(s => s.IdempotencyKey == idempotencyKey, cancellationToken)
                    ?? throw new InvalidOperationException("Failed to persist or retrieve immutable SummaryLog.");
                
                isIdempotentRetry = true;
                messageParts = SplitPersistedContentIntoParts(existingSummary.FormattedReportContent);
                totalBranches = existingSummary.TotalActiveBranches;
                completedBranches = existingSummary.CompletedBranches;
                completionPercentage = existingSummary.CompletionPercentage;
            }
        }
        else
        {
            messageParts = SplitPersistedContentIntoParts(existingSummary.FormattedReportContent);
            totalBranches = existingSummary.TotalActiveBranches;
            completedBranches = existingSummary.CompletedBranches;
            completionPercentage = existingSummary.CompletionPercentage;
        }

        // 2. Resolve configured active destination Telegram groups
        // Requirement: Do NOT send reports to source submission groups!
        var destinationGroups = await _dbContext.TelegramGroups
            .Where(g => g.ActivityId == slot.ActivityId &&
                        g.GroupRole == TelegramGroupRole.Destination &&
                        g.IsActive)
            .ToListAsync(cancellationToken);

        if (destinationGroups.Count == 0)
        {
            slot.SlotStatus = ReportDeliveryStatus.Failed;
            slot.StatusNotes = "No active Telegram destination group configured for this activity.";
            slot.UpdatedAtUtc = nowUtc;
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new SlotExecutionResult(
                ReportingSlotId: slot.ReportingSlotId,
                ActivityId: slot.ActivityId,
                ActivityCode: activity.ActivityCode,
                ReportingDate: slot.ReportingDate,
                ScheduledSlotTime: slot.ScheduledSlotTime,
                Status: ReportDeliveryStatus.Failed,
                TotalBranches: totalBranches,
                CompletedBranches: completedBranches,
                CompletionPercentage: completionPercentage,
                DestinationGroupsTargeted: 0,
                DestinationGroupsDelivered: 0,
                MessagePartsSent: 0,
                IsIdempotentRetry: isIdempotentRetry,
                StatusNotes: slot.StatusNotes,
                ErrorMessage: slot.StatusNotes);
        }

        // 3. Deliver to each destination group tracking multipart delivery and confirmed IDs
        int deliveredGroupsCount = 0;
        int totalPartsSent = 0;
        bool anyFailed = false;

        foreach (var destGroup in destinationGroups)
        {
            string deliveryKey = ReportDeliveryLog.BuildDeliveryIdempotencyKey(idempotencyKey, destGroup.TelegramGroupId);

            var delivery = existingSummary.Deliveries.FirstOrDefault(d => d.IdempotencyKey == deliveryKey)
                ?? await _dbContext.ReportDeliveryLogs.SingleOrDefaultAsync(d => d.IdempotencyKey == deliveryKey, cancellationToken);

            if (delivery is null)
            {
                delivery = new ReportDeliveryLog
                {
                    ReportId = existingSummary.ReportId,
                    ActivityId = slot.ActivityId,
                    ReportingDate = slot.ReportingDate,
                    ScheduledSlotTime = slot.ScheduledSlotTime,
                    DestinationGroupId = destGroup.TelegramGroupId,
                    DestinationTelegramChatId = destGroup.TelegramChatId,
                    IdempotencyKey = deliveryKey,
                    DeliveryStatus = ReportDeliveryStatus.DeliveryPending,
                    DeliveryAttempts = 0,
                    CreatedAtUtc = nowUtc
                };

                _dbContext.ReportDeliveryLogs.Add(delivery);
                if (!existingSummary.Deliveries.Contains(delivery))
                {
                    existingSummary.Deliveries.Add(delivery);
                }
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            if (delivery.DeliveryStatus == ReportDeliveryStatus.Delivered)
            {
                deliveredGroupsCount++;
                continue;
            }

            // Multipart recovery: inspect already confirmed Telegram message IDs
            // If parts 1..K were sent, resume from part K+1
            var confirmedIds = ParseDeliveredMessageIds(delivery.DeliveredTelegramMessageIds);
            int startPartIndex = confirmedIds.Count;

            bool destGroupFailed = false;

            for (int i = startPartIndex; i < messageParts.Count; i++)
            {
                var sendResult = await _telegramBotClient.SendMessageAsync(
                    destGroup.TelegramChatId,
                    messageParts[i],
                    cancellationToken: cancellationToken);

                if (sendResult.IsSuccess && sendResult.SentTelegramMessageId.HasValue)
                {
                    confirmedIds.Add(sendResult.SentTelegramMessageId.Value.ToString());
                    delivery.DeliveredTelegramMessageIds = string.Join(",", confirmedIds);
                    delivery.UpdatedAtUtc = nowUtc;
                    totalPartsSent++;
                }
                else
                {
                    destGroupFailed = true;
                    anyFailed = true;
                    string sanitizedErr = SanitizeError(sendResult.ErrorMessage ?? "Telegram send error.");
                    delivery.RecordFailedAttempt(sanitizedErr, nowUtc);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    break;
                }
            }

            if (!destGroupFailed && confirmedIds.Count == messageParts.Count)
            {
                delivery.MarkDelivered(string.Join(",", confirmedIds), nowUtc);
                await _dbContext.SaveChangesAsync(cancellationToken);
                deliveredGroupsCount++;
            }
        }

        // 4. Update overall slot status
        if (!anyFailed && deliveredGroupsCount == destinationGroups.Count)
        {
            slot.SlotStatus = ReportDeliveryStatus.Delivered;
            slot.StatusNotes = $"Successfully delivered to {deliveredGroupsCount} destination group(s).";
        }
        else
        {
            slot.SlotStatus = ReportDeliveryStatus.Failed;
            slot.StatusNotes = $"Delivered to {deliveredGroupsCount} of {destinationGroups.Count} destination group(s); retry pending.";
        }

        slot.UpdatedAtUtc = nowUtc;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new SlotExecutionResult(
            ReportingSlotId: slot.ReportingSlotId,
            ActivityId: slot.ActivityId,
            ActivityCode: activity.ActivityCode,
            ReportingDate: slot.ReportingDate,
            ScheduledSlotTime: slot.ScheduledSlotTime,
            Status: slot.SlotStatus,
            TotalBranches: totalBranches,
            CompletedBranches: completedBranches,
            CompletionPercentage: completionPercentage,
            DestinationGroupsTargeted: destinationGroups.Count,
            DestinationGroupsDelivered: deliveredGroupsCount,
            MessagePartsSent: totalPartsSent,
            IsIdempotentRetry: isIdempotentRetry,
            StatusNotes: slot.StatusNotes,
            ErrorMessage: anyFailed ? slot.StatusNotes : null);
    }

    private static IReadOnlyList<string> SplitPersistedContentIntoParts(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<string>();
        }

        return content.Split(new[] { "\n\n---\n\n" }, StringSplitOptions.None);
    }

    private static List<string> ParseDeliveredMessageIds(string? deliveredIds)
    {
        if (string.IsNullOrWhiteSpace(deliveredIds))
        {
            return new List<string>();
        }

        return deliveredIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private string SanitizeError(string rawError)
    {
        var options = _botOptionsMonitor.CurrentValue;
        return TelegramSecretMasker.SanitizeForLogging(
            rawError,
            options.BotToken,
            options.WebhookSecretToken);
    }
}
