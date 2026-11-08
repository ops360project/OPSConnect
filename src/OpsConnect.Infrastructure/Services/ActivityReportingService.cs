using Microsoft.EntityFrameworkCore;
using OpsConnect.Application.Common;
using OpsConnect.Application.Reporting;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Organisation;
using OpsConnect.Infrastructure.Persistence;

namespace OpsConnect.Infrastructure.Services;

/// <summary>
/// Domain-agnostic reporting aggregation service providing activity, hierarchy, and branch-level reporting summaries.
/// </summary>
public sealed class ActivityReportingService : IActivityReportingService
{
    private readonly OpsConnectDbContext _dbContext;
    private readonly IBusinessClock _businessClock;

    public ActivityReportingService(
        OpsConnectDbContext dbContext,
        IBusinessClock businessClock)
    {
        _dbContext = dbContext;
        _businessClock = businessClock;
    }

    public async Task<ActivityReportSummaryDto> GetActivitySummaryReportAsync(
        ReportingFilterQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = (query ?? new ReportingFilterQuery()).Normalize();
        var snapshot = await LoadReportingSnapshotAsync(normalizedQuery, cancellationToken);

        var zoneSummaries = BuildZoneSummaries(snapshot.EvaluatedBranches, includeChildren: true);

        // Calculate activity-level metrics across all evaluated branches in scope
        var metrics = CalculateMetrics(snapshot.EvaluatedBranches);

        return new ActivityReportSummaryDto(
            ActivityId: snapshot.Activity.ActivityId,
            ActivityCode: snapshot.Activity.ActivityCode,
            ActivityName: snapshot.Activity.ActivityName,
            ReportingDate: snapshot.ReportingDate,
            TimeZoneId: snapshot.TimeZoneId,
            GeneratedAtUtc: _businessClock.UtcNow,
            TotalBranches: metrics.Total,
            CompletedBranches: metrics.Completed,
            SubmittedPendingBranches: metrics.SubmittedPending,
            NotReportedBranches: metrics.NotReported,
            PendingBranches: metrics.PendingCount,
            CompletionPercentage: metrics.CompletionPercentage,
            IsEmptyPopulation: metrics.IsEmpty,
            IsFullyCompleted: metrics.IsFullyCompleted,
            TotalZones: zoneSummaries.Count,
            TotalStates: zoneSummaries.Sum(z => z.States?.Count ?? 0),
            TotalRegions: zoneSummaries.Sum(z => z.States?.Sum(s => s.Regions?.Count ?? 0) ?? 0),
            Zones: zoneSummaries,
            PendingReasons: CalculatePendingReasons(snapshot.EvaluatedBranches));
    }

    public async Task<IReadOnlyList<ZoneReportSummaryDto>> GetZoneSummariesAsync(
        ReportingFilterQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = (query ?? new ReportingFilterQuery()).Normalize();
        var snapshot = await LoadReportingSnapshotAsync(normalizedQuery, cancellationToken);

        return BuildZoneSummaries(snapshot.EvaluatedBranches, includeChildren: false);
    }

    public async Task<IReadOnlyList<StateReportSummaryDto>> GetStateSummariesAsync(
        ReportingFilterQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = (query ?? new ReportingFilterQuery()).Normalize();
        var snapshot = await LoadReportingSnapshotAsync(normalizedQuery, cancellationToken);

        return BuildStateSummaries(snapshot.EvaluatedBranches, includeRegions: false);
    }

    public async Task<IReadOnlyList<RegionReportSummaryDto>> GetRegionSummariesAsync(
        ReportingFilterQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = (query ?? new ReportingFilterQuery()).Normalize();
        var snapshot = await LoadReportingSnapshotAsync(normalizedQuery, cancellationToken);

        return BuildRegionSummaries(snapshot.EvaluatedBranches);
    }

    public async Task<BranchReportDetailsResponseDto> GetBranchReportDetailsAsync(
        ReportingFilterQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = (query ?? new ReportingFilterQuery()).Normalize();
        var snapshot = await LoadReportingSnapshotAsync(normalizedQuery, cancellationToken);

        IEnumerable<BranchReportDetailDto> filtered = snapshot.EvaluatedBranches;

        if (normalizedQuery.Status.HasValue)
        {
            filtered = filtered.Where(b => b.CurrentStatus == normalizedQuery.Status.Value);
        }

        if (normalizedQuery.BranchCode is not null)
        {
            filtered = filtered.Where(b => string.Equals(b.BranchCode, normalizedQuery.BranchCode, StringComparison.OrdinalIgnoreCase));
        }

        if (normalizedQuery.SearchTerm is not null)
        {
            filtered = filtered.Where(b =>
                b.BranchCode.Contains(normalizedQuery.SearchTerm, StringComparison.OrdinalIgnoreCase) ||
                b.BranchName.Contains(normalizedQuery.SearchTerm, StringComparison.OrdinalIgnoreCase));
        }

        var orderedQuery = filtered
            .OrderBy(b => b.ZoneCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.StateCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.RegionCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.BranchCode, StringComparer.OrdinalIgnoreCase);

        var allMatching = orderedQuery.ToList();
        var totalMatching = allMatching.Count;

        IReadOnlyList<BranchReportDetailDto> pagedItems;
        if (normalizedQuery.Page.HasValue || normalizedQuery.PageSize.HasValue)
        {
            int page = normalizedQuery.Page ?? 1;
            int pageSize = normalizedQuery.PageSize ?? 100;
            pagedItems = allMatching.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        }
        else
        {
            pagedItems = allMatching;
        }

        return new BranchReportDetailsResponseDto(
            ActivityId: snapshot.Activity.ActivityId,
            ActivityCode: snapshot.Activity.ActivityCode,
            ReportingDate: snapshot.ReportingDate,
            TotalMatchingBranches: totalMatching,
            Page: normalizedQuery.Page,
            PageSize: normalizedQuery.PageSize,
            Branches: pagedItems);
    }

    private async Task<ReportingDataSnapshot> LoadReportingSnapshotAsync(
        ReportingFilterQuery normalizedQuery,
        CancellationToken cancellationToken)
    {
        var activity = await _dbContext.Activities
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.ActivityCode == normalizedQuery.ActivityCode, cancellationToken)
            ?? throw new KeyNotFoundException($"Activity '{normalizedQuery.ActivityCode}' was not found.");

        var timeZoneId = await ResolveActivityTimeZoneAsync(activity.ActivityId, normalizedQuery.ReportingDate, cancellationToken);
        var reportingDate = normalizedQuery.ReportingDate ?? _businessClock.GetBusinessDate(timeZoneId);

        // Efficient query loading only active mapped branches valid as of the reporting date
        var branches = await _dbContext.Branches
            .AsNoTracking()
            .Include(b => b.Zone)
            .Include(b => b.State)
            .Include(b => b.Region)
            .Include(b => b.MappingHistories)
            .Where(b => b.IsActive &&
                        b.Zone.IsActive &&
                        b.State.IsActive &&
                        b.Region.IsActive &&
                        b.MappingHistories.Any(h =>
                            h.EffectiveFromDate <= reportingDate &&
                            (h.EffectiveToDate == null || h.EffectiveToDate >= reportingDate)))
            .ToListAsync(cancellationToken);

        // Load activity statuses strictly for this reporting date (no carry-forward from previous dates)
        var statusRows = await _dbContext.ActivityStatuses
            .AsNoTracking()
            .Where(s => s.ActivityId == activity.ActivityId && s.ReportingDate == reportingDate)
            .ToDictionaryAsync(s => s.BranchId, cancellationToken);

        // Load available reopening permissions for this activity and reporting date
        var availablePermissionsBranchIds = (await _dbContext.StatusReopenPermissions
            .AsNoTracking()
            .Where(p => p.ActivityId == activity.ActivityId &&
                        p.ReportingDate == reportingDate &&
                        p.PermissionStatus == ReopenPermissionStatus.Available)
            .Select(p => p.BranchId)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var evaluatedBranches = new List<BranchReportDetailDto>(branches.Count);

        foreach (var branch in branches)
        {
            var mapping = branch.ResolveMappingAsOf(reportingDate);
            if (mapping is null)
            {
                continue;
            }

            // Hierarchy-level filtering
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

            bool hasRow = statusRows.TryGetValue(branch.BranchId, out var statusRow);
            var currentStatus = hasRow ? statusRow!.CurrentStatus : BranchActivityStatus.NotReported;

            evaluatedBranches.Add(new BranchReportDetailDto(
                BranchId: branch.BranchId,
                BranchCode: branch.BranchCode,
                BranchName: branch.BranchName,
                ZoneId: mapping.ZoneId,
                ZoneCode: mapping.ZoneCode,
                ZoneName: mapping.ZoneName,
                StateId: mapping.StateId,
                StateCode: mapping.StateCode,
                StateName: mapping.StateName,
                RegionId: mapping.RegionId,
                RegionCode: mapping.RegionCode,
                RegionName: mapping.RegionName,
                ReportingDate: reportingDate,
                CurrentStatus: currentStatus,
                IsDerivedNotReported: !hasRow,
                ReopenedCount: statusRow?.ReopenedCount ?? 0,
                HasAvailableReopenPermission: availablePermissionsBranchIds.Contains(branch.BranchId),
                LastTelegramMessageLogId: statusRow?.LastTelegramMessageLogId,
                LastUpdatedByTelegramUserId: statusRow?.LastUpdatedByTelegramUserId,
                LastUpdatedByDisplayName: statusRow?.LastUpdatedByDisplayName,
                LastUpdatedAtUtc: statusRow?.UpdatedAtUtc ?? statusRow?.CreatedAtUtc,
                PendingReason: currentStatus == BranchActivityStatus.Pending ? statusRow?.CurrentPendingReason : null,
                PendingReasonDescription: currentStatus == BranchActivityStatus.Pending ? statusRow?.CurrentPendingReasonDescription : null));
        }

        return new ReportingDataSnapshot(activity, reportingDate, timeZoneId, evaluatedBranches);
    }

    private static IReadOnlyList<ZoneReportSummaryDto> BuildZoneSummaries(
        IReadOnlyList<BranchReportDetailDto> evaluatedBranches,
        bool includeChildren)
    {
        var zoneGroups = evaluatedBranches
            .GroupBy(b => new { b.ZoneId, b.ZoneCode, b.ZoneName })
            .OrderBy(g => g.Key.ZoneCode, StringComparer.OrdinalIgnoreCase);

        var result = new List<ZoneReportSummaryDto>();

        foreach (var zoneGroup in zoneGroups)
        {
            var zoneMetrics = CalculateMetrics(zoneGroup.ToList());

            IReadOnlyList<StateReportSummaryDto>? states = includeChildren
                ? BuildStateSummaries(zoneGroup.ToList(), includeRegions: true)
                : null;

            result.Add(new ZoneReportSummaryDto(
                ZoneId: zoneGroup.Key.ZoneId,
                ZoneCode: zoneGroup.Key.ZoneCode,
                ZoneName: zoneGroup.Key.ZoneName,
                TotalBranches: zoneMetrics.Total,
                CompletedBranches: zoneMetrics.Completed,
                SubmittedPendingBranches: zoneMetrics.SubmittedPending,
                NotReportedBranches: zoneMetrics.NotReported,
                PendingBranches: zoneMetrics.PendingCount,
                CompletionPercentage: zoneMetrics.CompletionPercentage,
                IsEmptyPopulation: zoneMetrics.IsEmpty,
                IsFullyCompleted: zoneMetrics.IsFullyCompleted,
                States: states,
                PendingReasons: CalculatePendingReasons(zoneGroup.ToList())));
        }

        return result;
    }

    private static IReadOnlyList<StateReportSummaryDto> BuildStateSummaries(
        IReadOnlyList<BranchReportDetailDto> evaluatedBranches,
        bool includeRegions)
    {
        var stateGroups = evaluatedBranches
            .GroupBy(b => new { b.StateId, b.StateCode, b.StateName, b.ZoneId, b.ZoneCode, b.ZoneName })
            .OrderBy(g => g.Key.ZoneCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.StateCode, StringComparer.OrdinalIgnoreCase);

        var result = new List<StateReportSummaryDto>();

        foreach (var stateGroup in stateGroups)
        {
            var stateMetrics = CalculateMetrics(stateGroup.ToList());

            IReadOnlyList<RegionReportSummaryDto>? regions = includeRegions
                ? BuildRegionSummaries(stateGroup.ToList())
                : null;

            result.Add(new StateReportSummaryDto(
                StateId: stateGroup.Key.StateId,
                StateCode: stateGroup.Key.StateCode,
                StateName: stateGroup.Key.StateName,
                ZoneId: stateGroup.Key.ZoneId,
                ZoneCode: stateGroup.Key.ZoneCode,
                ZoneName: stateGroup.Key.ZoneName,
                TotalBranches: stateMetrics.Total,
                CompletedBranches: stateMetrics.Completed,
                SubmittedPendingBranches: stateMetrics.SubmittedPending,
                NotReportedBranches: stateMetrics.NotReported,
                PendingBranches: stateMetrics.PendingCount,
                CompletionPercentage: stateMetrics.CompletionPercentage,
                IsEmptyPopulation: stateMetrics.IsEmpty,
                IsFullyCompleted: stateMetrics.IsFullyCompleted,
                Regions: regions,
                PendingReasons: CalculatePendingReasons(stateGroup.ToList())));
        }

        return result;
    }

    private static IReadOnlyList<RegionReportSummaryDto> BuildRegionSummaries(
        IReadOnlyList<BranchReportDetailDto> evaluatedBranches)
    {
        var regionGroups = evaluatedBranches
            .GroupBy(b => new { b.RegionId, b.RegionCode, b.RegionName, b.StateId, b.StateCode, b.StateName, b.ZoneId, b.ZoneCode, b.ZoneName })
            .OrderBy(g => g.Key.ZoneCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.StateCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.RegionCode, StringComparer.OrdinalIgnoreCase);

        var result = new List<RegionReportSummaryDto>();

        foreach (var regionGroup in regionGroups)
        {
            var regionMetrics = CalculateMetrics(regionGroup.ToList());

            result.Add(new RegionReportSummaryDto(
                RegionId: regionGroup.Key.RegionId,
                RegionCode: regionGroup.Key.RegionCode,
                RegionName: regionGroup.Key.RegionName,
                StateId: regionGroup.Key.StateId,
                StateCode: regionGroup.Key.StateCode,
                StateName: regionGroup.Key.StateName,
                ZoneId: regionGroup.Key.ZoneId,
                ZoneCode: regionGroup.Key.ZoneCode,
                ZoneName: regionGroup.Key.ZoneName,
                TotalBranches: regionMetrics.Total,
                CompletedBranches: regionMetrics.Completed,
                SubmittedPendingBranches: regionMetrics.SubmittedPending,
                NotReportedBranches: regionMetrics.NotReported,
                PendingBranches: regionMetrics.PendingCount,
                CompletionPercentage: regionMetrics.CompletionPercentage,
                IsEmptyPopulation: regionMetrics.IsEmpty,
                IsFullyCompleted: regionMetrics.IsFullyCompleted,
                PendingReasons: CalculatePendingReasons(regionGroup.ToList())));
        }

        return result;
    }

    private static IReadOnlyList<PendingReasonCountDto> CalculatePendingReasons(
        IReadOnlyList<BranchReportDetailDto> branches)
    {
        var pendingBranches = branches
            .Where(b => b.CurrentStatus == BranchActivityStatus.Pending)
            .ToList();

        if (pendingBranches.Count == 0)
        {
            return Array.Empty<PendingReasonCountDto>();
        }

        int totalPending = pendingBranches.Count;

        return pendingBranches
            .GroupBy(b => b.PendingReason ?? PendingReasonCode.None)
            .Select(g =>
            {
                var code = g.Key;
                var name = code.ToDisplayName();
                var count = g.Count();
                var pct = Math.Round((decimal)count * 100m / totalPending, 2, MidpointRounding.AwayFromZero);
                return new PendingReasonCountDto(code, name, count, pct);
            })
            .OrderByDescending(r => r.BranchCount)
            .ThenBy(r => r.ReasonName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static MetricsResult CalculateMetrics(IReadOnlyList<BranchReportDetailDto> branches)
    {
        int total = branches.Count;
        int completed = branches.Count(b => b.CurrentStatus == BranchActivityStatus.Completed);
        int submittedPending = branches.Count(b => b.CurrentStatus == BranchActivityStatus.Pending);
        int notReported = branches.Count(b => b.CurrentStatus == BranchActivityStatus.NotReported);
        
        // Section 2 invariant: Pending count = Pending + NotReported
        int pendingCount = submittedPending + notReported;

        decimal completionPct = total > 0
            ? Math.Round((decimal)completed * 100m / total, 2, MidpointRounding.AwayFromZero)
            : 0.00m;

        bool isEmpty = total == 0;
        bool isFullyCompleted = !isEmpty && completed == total;

        return new MetricsResult(total, completed, submittedPending, notReported, pendingCount, completionPct, isEmpty, isFullyCompleted);
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

    private sealed record ReportingDataSnapshot(
        ActivityMaster Activity,
        DateOnly ReportingDate,
        string TimeZoneId,
        IReadOnlyList<BranchReportDetailDto> EvaluatedBranches);

    private sealed record MetricsResult(
        int Total,
        int Completed,
        int SubmittedPending,
        int NotReported,
        int PendingCount,
        decimal CompletionPercentage,
        bool IsEmpty,
        bool IsFullyCompleted);
}
