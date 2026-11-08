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

        var allMappedItems = new List<BranchActivityStatusItemDto>(activeBranches.Count);

        foreach (var branch in activeBranches)
        {
            var mapping = branch.ResolveMappingAsOf(reportingDate);
            if (mapping is null)
            {
                continue;
            }

            bool hasRow = statusesByBranchId.TryGetValue(branch.BranchId, out var statusRow);
            var currentStatus = hasRow ? statusRow!.CurrentStatus : BranchActivityStatus.NotReported;

            allMappedItems.Add(new BranchActivityStatusItemDto(
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
                LastUpdatedAtUtc: statusRow?.UpdatedAtUtc ?? statusRow?.CreatedAtUtc,
                CurrentPendingReason: currentStatus == BranchActivityStatus.Pending ? statusRow?.CurrentPendingReason : null,
                CurrentPendingReasonDescription: currentStatus == BranchActivityStatus.Pending ? statusRow?.CurrentPendingReasonDescription : null));
        }

        // Summary counts always represent the full active mapped branch population on the reporting date
        int total = allMappedItems.Count;
        int completed = allMappedItems.Count(i => i.CurrentStatus == BranchActivityStatus.Completed);
        int pending = allMappedItems.Count(i => i.CurrentStatus == BranchActivityStatus.Pending);
        int notReported = allMappedItems.Count(i => i.CurrentStatus == BranchActivityStatus.NotReported);
        decimal completionPct = total > 0
            ? Math.Round((decimal)completed * 100m / total, 2, MidpointRounding.AwayFromZero)
            : 0m;

        IEnumerable<BranchActivityStatusItemDto> filtered = allMappedItems;

        if (normalizedQuery.ZoneCode is not null)
        {
            filtered = filtered.Where(i => string.Equals(i.ZoneCode, normalizedQuery.ZoneCode, StringComparison.OrdinalIgnoreCase));
        }

        if (normalizedQuery.StateCode is not null)
        {
            filtered = filtered.Where(i => string.Equals(i.StateCode, normalizedQuery.StateCode, StringComparison.OrdinalIgnoreCase));
        }

        if (normalizedQuery.RegionCode is not null)
        {
            filtered = filtered.Where(i => string.Equals(i.RegionCode, normalizedQuery.RegionCode, StringComparison.OrdinalIgnoreCase));
        }

        if (normalizedQuery.BranchCode is not null)
        {
            filtered = filtered.Where(i => string.Equals(i.BranchCode, normalizedQuery.BranchCode, StringComparison.OrdinalIgnoreCase));
        }

        if (normalizedQuery.Status.HasValue)
        {
            filtered = filtered.Where(i => i.CurrentStatus == normalizedQuery.Status.Value);
        }

        if (normalizedQuery.SearchTerm is not null)
        {
            filtered = filtered.Where(i =>
                i.BranchCode.Contains(normalizedQuery.SearchTerm, StringComparison.OrdinalIgnoreCase) ||
                i.BranchName.Contains(normalizedQuery.SearchTerm, StringComparison.OrdinalIgnoreCase));
        }

        IEnumerable<BranchActivityStatusItemDto> orderedQuery = filtered
            .OrderBy(i => i.ZoneCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.StateCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.RegionCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.BranchCode, StringComparer.OrdinalIgnoreCase);

        if (normalizedQuery.Page.HasValue || normalizedQuery.PageSize.HasValue)
        {
            int page = normalizedQuery.Page ?? 1;
            int pageSize = normalizedQuery.PageSize ?? 100;
            orderedQuery = orderedQuery.Skip((page - 1) * pageSize).Take(pageSize);
        }

        var orderedItems = orderedQuery.ToList();

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

    public async Task<BranchActivityStatusItemDto?> GetBranchCurrentStatusAsync(
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
            .Include(b => b.Zone)
            .Include(b => b.State)
            .Include(b => b.Region)
            .Include(b => b.MappingHistories)
            .SingleOrDefaultAsync(b => b.BranchCode == normalizedBranchCode, cancellationToken);

        if (branch is null)
        {
            return null;
        }

        var timeZoneId = await ResolveActivityTimeZoneAsync(activity.ActivityId, reportingDate, cancellationToken);
        var effectiveDate = reportingDate ?? _businessClock.GetBusinessDate(timeZoneId);

        var mapping = branch.ResolveMappingAsOf(effectiveDate);
        var statusRow = await _dbContext.ActivityStatuses
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

        bool hasRow = statusRow is not null;
        return new BranchActivityStatusItemDto(
            BranchId: branch.BranchId,
            BranchCode: branch.BranchCode,
            BranchName: branch.BranchName,
            RegionId: mapping?.RegionId ?? branch.RegionId,
            RegionCode: mapping?.RegionCode ?? branch.Region.RegionCode,
            RegionName: mapping?.RegionName ?? branch.Region.RegionName,
            StateId: mapping?.StateId ?? branch.StateId,
            StateCode: mapping?.StateCode ?? branch.State.StateCode,
            StateName: mapping?.StateName ?? branch.State.StateName,
            ZoneId: mapping?.ZoneId ?? branch.ZoneId,
            ZoneCode: mapping?.ZoneCode ?? branch.Zone.ZoneCode,
            ZoneName: mapping?.ZoneName ?? branch.Zone.ZoneName,
            ReportingDate: effectiveDate,
            CurrentStatus: hasRow ? statusRow!.CurrentStatus : BranchActivityStatus.NotReported,
            IsDerivedNotReported: !hasRow,
            ReopenedCount: statusRow?.ReopenedCount ?? 0,
            HasAvailableReopenPermission: hasAvailablePermission,
            LastTelegramMessageLogId: statusRow?.LastTelegramMessageLogId,
            LastUpdatedByTelegramUserId: statusRow?.LastUpdatedByTelegramUserId,
            LastUpdatedByDisplayName: statusRow?.LastUpdatedByDisplayName,
            LastUpdatedAtUtc: statusRow?.UpdatedAtUtc ?? statusRow?.CreatedAtUtc,
            CurrentPendingReason: (hasRow && statusRow!.CurrentStatus == BranchActivityStatus.Pending) ? statusRow.CurrentPendingReason : null,
            CurrentPendingReasonDescription: (hasRow && statusRow!.CurrentStatus == BranchActivityStatus.Pending) ? statusRow.CurrentPendingReasonDescription : null);
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
                h.RecordedAtUtc,
                h.PendingReason,
                h.PendingReasonDescription))
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
