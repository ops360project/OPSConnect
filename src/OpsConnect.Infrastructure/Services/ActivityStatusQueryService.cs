using Microsoft.EntityFrameworkCore;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Common;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Organisation;
using OpsConnect.Infrastructure.Persistence;

namespace OpsConnect.Infrastructure.Services;

public sealed class ActivityStatusQueryService : IActivityStatusQueryService
{
    private readonly OpsConnectDbContext _dbContext;
    private readonly IBusinessClock _businessClock;

    public ActivityStatusQueryService(
        OpsConnectDbContext dbContext,
        IBusinessClock businessClock)
    {
        _dbContext = dbContext;
        _businessClock = businessClock;
    }

    public async Task<ActivityDailyStatusSnapshotDto> GetBranchStatusesAsync(
        ActivityStatusFilterQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = (query ?? new ActivityStatusFilterQuery()).Normalize();

        var activity = await _dbContext.Activities
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.ActivityCode == normalizedQuery.ActivityCode, cancellationToken)
            ?? throw new KeyNotFoundException($"Activity '{normalizedQuery.ActivityCode}' was not found.");

        var timeZoneId = await ResolveActivityTimeZoneAsync(activity.ActivityId, normalizedQuery.ReportingDate, cancellationToken);
        var reportingDate = normalizedQuery.ReportingDate ?? _businessClock.GetBusinessDate(timeZoneId);

        var activeBranches = await _dbContext.Branches
            .AsNoTracking()
            .Include(b => b.Zone)
            .Include(b => b.State)
            .Include(b => b.Region)
            .Include(b => b.MappingHistories)
            .Where(b => b.IsActive && b.Zone.IsActive && b.State.IsActive && b.Region.IsActive)
            .ToListAsync(cancellationToken);

        var statusesByBranchId = await _dbContext.ActivityStatuses
            .AsNoTracking()
            .Where(s => s.ActivityId == activity.ActivityId && s.ReportingDate == reportingDate)
            .ToDictionaryAsync(s => s.BranchId, cancellationToken);

        var availablePermissionsBranchIds = (await _dbContext.StatusReopenPermissions
            .AsNoTracking()
            .Where(p => p.ActivityId == activity.ActivityId &&
                        p.ReportingDate == reportingDate &&
                        p.PermissionStatus == ReopenPermissionStatus.Available)
            .Select(p => p.BranchId)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var items = new List<BranchActivityStatusItemDto>(activeBranches.Count);

        foreach (var branch in activeBranches)
        {
            var mapping = branch.ResolveMappingAsOf(reportingDate);
            if (mapping is null)
            {
                continue;
            }

            if (normalizedQuery.ZoneCode is not null &&
                !string.Equals(mapping.ZoneCode, normalizedQuery.ZoneCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (normalizedQuery.StateCode is not null &&
                !string.Equals(mapping.StateCode, normalizedQuery.StateCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (normalizedQuery.RegionCode is not null &&
                !string.Equals(mapping.RegionCode, normalizedQuery.RegionCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (normalizedQuery.SearchTerm is not null &&
                !branch.BranchCode.Contains(normalizedQuery.SearchTerm, StringComparison.OrdinalIgnoreCase) &&
                !branch.BranchName.Contains(normalizedQuery.SearchTerm, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool hasRow = statusesByBranchId.TryGetValue(branch.BranchId, out var statusRow);
            var currentStatus = hasRow ? statusRow!.CurrentStatus : BranchActivityStatus.NotReported;

            if (normalizedQuery.Status.HasValue && currentStatus != normalizedQuery.Status.Value)
            {
                continue;
            }

            items.Add(new BranchActivityStatusItemDto(
                BranchId: branch.BranchId,
                BranchCode: branch.BranchCode,
                BranchName: branch.BranchName,
                RegionId: mapping.RegionId,
                RegionCode: mapping.RegionCode,
                RegionName: mapping.RegionName,
                StateId: mapping.StateId,
                StateCode: mapping.StateCode,
                StateName: mapping.StateName,
                ZoneId: mapping.ZoneId,
                ZoneCode: mapping.ZoneCode,
                ZoneName: mapping.ZoneName,
                ReportingDate: reportingDate,
                CurrentStatus: currentStatus,
                IsDerivedNotReported: !hasRow,
                ReopenedCount: statusRow?.ReopenedCount ?? 0,
                HasAvailableReopenPermission: availablePermissionsBranchIds.Contains(branch.BranchId),
                LastTelegramMessageLogId: statusRow?.LastTelegramMessageLogId,
                LastUpdatedByTelegramUserId: statusRow?.LastUpdatedByTelegramUserId,
                LastUpdatedByDisplayName: statusRow?.LastUpdatedByDisplayName,
                LastUpdatedAtUtc: statusRow?.UpdatedAtUtc ?? statusRow?.CreatedAtUtc));
        }

        var orderedItems = items
            .OrderBy(i => i.ZoneCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.StateCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.RegionCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.BranchCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int total = orderedItems.Count;
        int completed = orderedItems.Count(i => i.CurrentStatus == BranchActivityStatus.Completed);
        int pending = orderedItems.Count(i => i.CurrentStatus == BranchActivityStatus.Pending);
        int notReported = orderedItems.Count(i => i.CurrentStatus == BranchActivityStatus.NotReported);
        decimal completionPct = total > 0
            ? Math.Round((decimal)completed * 100m / total, 2, MidpointRounding.AwayFromZero)
            : 0m;

        return new ActivityDailyStatusSnapshotDto(
            ActivityId: activity.ActivityId,
            ActivityCode: activity.ActivityCode,
            ActivityName: activity.ActivityName,
            ReportingDate: reportingDate,
            TimeZoneId: timeZoneId,
            TotalActiveBranches: total,
            CompletedBranches: completed,
            PendingBranches: pending,
            NotReportedBranches: notReported,
            CompletionPercentage: completionPct,
            Branches: orderedItems);
    }

    public async Task<BranchActivityHistoryResultDto?> GetBranchActivityHistoryAsync(
        string activityCode,
        string branchCode,
        DateOnly? reportingDate = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(activityCode) || string.IsNullOrWhiteSpace(branchCode))
        {
            return null;
        }

        string normalizedActivityCode;
        string normalizedBranchCode;
        try
        {
            normalizedActivityCode = ActivityMaster.ValidateActivityCode(activityCode);
            normalizedBranchCode = OrganisationCodeNormalizer.NormalizeAndValidateBranchCode(branchCode, nameof(branchCode));
        }
        catch (ArgumentException)
        {
            return null;
        }

        var activity = await _dbContext.Activities
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.ActivityCode == normalizedActivityCode, cancellationToken);

        if (activity is null)
        {
            return null;
        }

        var branch = await _dbContext.Branches
            .AsNoTracking()
            .SingleOrDefaultAsync(b => b.BranchCode == normalizedBranchCode, cancellationToken);

        if (branch is null)
        {
            return null;
        }

        var timeZoneId = await ResolveActivityTimeZoneAsync(activity.ActivityId, reportingDate, cancellationToken);
        var effectiveDate = reportingDate ?? _businessClock.GetBusinessDate(timeZoneId);

        var statusOnDate = await _dbContext.ActivityStatuses
            .AsNoTracking()
            .SingleOrDefaultAsync(
                s => s.ActivityId == activity.ActivityId &&
                     s.BranchId == branch.BranchId &&
                     s.ReportingDate == effectiveDate,
                cancellationToken);

        bool hasAvailablePermission = await _dbContext.StatusReopenPermissions
            .AsNoTracking()
            .AnyAsync(
                p => p.ActivityId == activity.ActivityId &&
                     p.BranchId == branch.BranchId &&
                     p.ReportingDate == effectiveDate &&
                     p.PermissionStatus == ReopenPermissionStatus.Available,
                cancellationToken);

        var historyQuery = _dbContext.ActivityHistories
            .AsNoTracking()
            .Where(h => h.ActivityId == activity.ActivityId && h.BranchId == branch.BranchId);

        if (reportingDate.HasValue)
        {
            historyQuery = historyQuery.Where(h => h.ReportingDate == effectiveDate);
        }

        var histories = await historyQuery
            .OrderBy(h => h.RecordedAtUtc)
            .ThenBy(h => h.ActivityHistoryId)
            .Select(h => new ActivityHistoryEntryDto(
                h.ActivityHistoryId,
                h.ActivityId,
                activity.ActivityCode,
                h.BranchId,
                branch.BranchCode,
                h.ReportingDate,
                h.PreviousStatus,
                h.NewStatus,
                h.Outcome,
                h.TelegramMessageLogId,
                h.ConsumedReopenPermissionId,
                h.TelegramUserId,
                h.TelegramUsername,
                h.TelegramDisplayName,
                h.Notes,
                h.RecordedAtUtc))
            .ToListAsync(cancellationToken);

        return new BranchActivityHistoryResultDto(
            ActivityId: activity.ActivityId,
            ActivityCode: activity.ActivityCode,
            BranchId: branch.BranchId,
            BranchCode: branch.BranchCode,
            BranchName: branch.BranchName,
            IsBranchActive: branch.IsActive,
            ReportingDate: effectiveDate,
            CurrentStatusOnDate: statusOnDate?.CurrentStatus ?? BranchActivityStatus.NotReported,
            IsDerivedNotReported: statusOnDate is null,
            ReopenedCountOnDate: statusOnDate?.ReopenedCount ?? 0,
            HasAvailableReopenPermissionOnDate: hasAvailablePermission,
            HistoryEntries: histories);
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
}
