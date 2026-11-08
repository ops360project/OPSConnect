using Microsoft.EntityFrameworkCore;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Common;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using OpsConnect.Infrastructure.Persistence;

namespace OpsConnect.Infrastructure.Services;

public sealed class StatusReopenPermissionService : IStatusReopenPermissionService
{
    private readonly OpsConnectDbContext _dbContext;
    private readonly IBusinessClock _businessClock;

    public StatusReopenPermissionService(
        OpsConnectDbContext dbContext,
        IBusinessClock businessClock)
    {
        _dbContext = dbContext;
        _businessClock = businessClock;
    }

    public async Task<StatusReopenPermissionOperationResult> GrantPermissionAsync(
        GrantStatusReopenPermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = request.ValidateAndNormalize();

        var ambientTransaction = _dbContext.Database.CurrentTransaction;
        if (ambientTransaction is not null)
        {
            const string savepointName = "GrantReopenSavepoint";
            await ambientTransaction.CreateSavepointAsync(savepointName, cancellationToken);
            try
            {
                var result = await ExecuteGrantAsync(normalized, cancellationToken);
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
                var result = await ExecuteGrantAsync(normalized, cancellationToken);
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

    private async Task<StatusReopenPermissionOperationResult> ExecuteGrantAsync(
        GrantStatusReopenPermissionRequest normalized,
        CancellationToken cancellationToken)
    {
        var activity = await _dbContext.Activities
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.ActivityCode == normalized.ActivityCode, cancellationToken);

        if (activity is null || !activity.IsActive)
        {
            return new StatusReopenPermissionOperationResult(
                IsSuccess: false,
                ErrorCode: "ActivityNotFoundOrInactive",
                ErrorMessage: activity is null
                    ? $"Activity '{normalized.ActivityCode}' was not found."
                    : $"Activity '{normalized.ActivityCode}' is inactive.",
                Permission: null);
        }

        var branch = await _dbContext.Branches
            .Include(b => b.Zone)
            .Include(b => b.State)
            .Include(b => b.Region)
            .Include(b => b.MappingHistories)
            .SingleOrDefaultAsync(b => b.BranchCode == normalized.BranchCode, cancellationToken);

        if (branch is null)
        {
            return new StatusReopenPermissionOperationResult(
                IsSuccess: false,
                ErrorCode: "BranchNotFound",
                ErrorMessage: $"Branch '{normalized.BranchCode}' was not found.",
                Permission: null);
        }

        if (!branch.CanSubmitActivityUpdate(out var inactiveReason))
        {
            return new StatusReopenPermissionOperationResult(
                IsSuccess: false,
                ErrorCode: "BranchInactive",
                ErrorMessage: inactiveReason,
                Permission: null);
        }

        var timeZoneId = await ResolveActivityTimeZoneAsync(activity.ActivityId, normalized.ReportingDate, cancellationToken);
        var reportingDate = normalized.ReportingDate ?? _businessClock.GetBusinessDate(timeZoneId);

        if (branch.ResolveMappingAsOf(reportingDate) is null)
        {
            return new StatusReopenPermissionOperationResult(
                IsSuccess: false,
                ErrorCode: "BranchUnmappedOnDate",
                ErrorMessage: $"Branch '{branch.BranchCode}' has no effective hierarchy mapping for reporting date {reportingDate:yyyy-MM-dd}.",
                Permission: null);
        }

        // Expire any stale Available permissions from earlier reporting dates for this branch and activity
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

        var existingAvailable = await _dbContext.StatusReopenPermissions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                p => p.BranchId == branch.BranchId &&
                     p.ActivityId == activity.ActivityId &&
                     p.ReportingDate == reportingDate &&
                     p.PermissionStatus == ReopenPermissionStatus.Available,
                cancellationToken);

        if (existingAvailable is not null)
        {
            return new StatusReopenPermissionOperationResult(
                IsSuccess: false,
                ErrorCode: "DuplicateAvailablePermission",
                ErrorMessage: $"An unconsumed Available reopening permission (ID {existingAvailable.PermissionId}) already exists for branch '{branch.BranchCode}', activity '{activity.ActivityCode}', and date {reportingDate:yyyy-MM-dd}.",
                Permission: MapToDto(existingAvailable, activity.ActivityCode, branch.BranchCode, branch.BranchName));
        }

        var nowUtc = _businessClock.UtcNow;
        var permission = StatusReopenPermission.Grant(
            branchId: branch.BranchId,
            activityId: activity.ActivityId,
            reportingDate: reportingDate,
            enabledBy: normalized.EnabledBy,
            reason: normalized.Reason,
            enabledAtUtc: nowUtc);

        _dbContext.StatusReopenPermissions.Add(permission);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new StatusReopenPermissionOperationResult(
            IsSuccess: true,
            ErrorCode: null,
            ErrorMessage: null,
            Permission: MapToDto(permission, activity.ActivityCode, branch.BranchCode, branch.BranchName));
    }

    public async Task<StatusReopenPermissionOperationResult> RevokePermissionAsync(
        RevokeStatusReopenPermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = request.ValidateAndNormalize();

        var permission = await _dbContext.StatusReopenPermissions
            .Include(p => p.Activity)
            .Include(p => p.Branch)
            .SingleOrDefaultAsync(p => p.PermissionId == normalized.PermissionId, cancellationToken);

        if (permission is null)
        {
            return new StatusReopenPermissionOperationResult(
                IsSuccess: false,
                ErrorCode: "PermissionNotFound",
                ErrorMessage: $"Reopening permission ID {normalized.PermissionId} was not found.",
                Permission: null);
        }

        try
        {
            permission.Revoke(normalized.RevokedBy, normalized.Reason, _businessClock.UtcNow);
        }
        catch (InvalidOperationException ex)
        {
            return new StatusReopenPermissionOperationResult(
                IsSuccess: false,
                ErrorCode: "InvalidPermissionState",
                ErrorMessage: ex.Message,
                Permission: MapToDto(
                    permission,
                    permission.Activity.ActivityCode,
                    permission.Branch.BranchCode,
                    permission.Branch.BranchName));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new StatusReopenPermissionOperationResult(
            IsSuccess: true,
            ErrorCode: null,
            ErrorMessage: null,
            Permission: MapToDto(
                permission,
                permission.Activity.ActivityCode,
                permission.Branch.BranchCode,
                permission.Branch.BranchName));
    }

    public async Task<int> ExpireStalePermissionsAsync(
        string? activityCode = null,
        DateOnly? asOfReportingDate = null,
        CancellationToken cancellationToken = default)
    {
        int? activityIdFilter = null;
        var timeZoneId = ActivityReportingConfiguration.DefaultTimeZoneId;

        if (!string.IsNullOrWhiteSpace(activityCode))
        {
            var normalizedCode = ActivityMaster.ValidateActivityCode(activityCode);
            var activity = await _dbContext.Activities
                .AsNoTracking()
                .SingleOrDefaultAsync(a => a.ActivityCode == normalizedCode, cancellationToken);

            if (activity is null)
            {
                return 0;
            }

            activityIdFilter = activity.ActivityId;
            timeZoneId = await ResolveActivityTimeZoneAsync(activity.ActivityId, asOfReportingDate, cancellationToken);
        }

        var effectiveDate = asOfReportingDate ?? _businessClock.GetBusinessDate(timeZoneId);

        var query = _dbContext.StatusReopenPermissions
            .Where(p => p.PermissionStatus == ReopenPermissionStatus.Available &&
                        p.ReportingDate < effectiveDate);

        if (activityIdFilter.HasValue)
        {
            query = query.Where(p => p.ActivityId == activityIdFilter.Value);
        }

        var stalePermissions = await query.ToListAsync(cancellationToken);
        if (stalePermissions.Count == 0)
        {
            return 0;
        }

        int expiredCount = StatusReopenPermission.ExpireStalePermissions(stalePermissions, effectiveDate);
        if (expiredCount > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return expiredCount;
    }

    public async Task<IReadOnlyList<StatusReopenPermissionDto>> GetPermissionsHistoryAsync(
        StatusReopenPermissionFilterQuery? filter = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedFilter = (filter ?? new StatusReopenPermissionFilterQuery()).Normalize();

        var query = _dbContext.StatusReopenPermissions
            .AsNoTracking()
            .Include(p => p.Activity)
            .Include(p => p.Branch)
            .AsQueryable();

        if (normalizedFilter.ActivityCode is not null)
        {
            query = query.Where(p => p.Activity.ActivityCode == normalizedFilter.ActivityCode);
        }

        if (normalizedFilter.BranchCode is not null)
        {
            query = query.Where(p => p.Branch.BranchCode == normalizedFilter.BranchCode);
        }

        if (normalizedFilter.ReportingDate.HasValue)
        {
            query = query.Where(p => p.ReportingDate == normalizedFilter.ReportingDate.Value);
        }

        if (normalizedFilter.Status.HasValue)
        {
            query = query.Where(p => p.PermissionStatus == normalizedFilter.Status.Value);
        }

        var records = await query
            .OrderByDescending(p => p.EnabledAtUtc)
            .ThenByDescending(p => p.PermissionId)
            .ToListAsync(cancellationToken);

        return records
            .Select(p => MapToDto(p, p.Activity.ActivityCode, p.Branch.BranchCode, p.Branch.BranchName))
            .ToList();
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

    private static StatusReopenPermissionDto MapToDto(
        StatusReopenPermission p,
        string activityCode,
        string branchCode,
        string branchName) =>
        new(
            PermissionId: p.PermissionId,
            ActivityId: p.ActivityId,
            ActivityCode: activityCode,
            BranchId: p.BranchId,
            BranchCode: branchCode,
            BranchName: branchName,
            ReportingDate: p.ReportingDate,
            PermissionStatus: p.PermissionStatus,
            EnabledBy: p.EnabledBy,
            EnabledAtUtc: p.EnabledAtUtc,
            Reason: p.Reason,
            ConsumedAtUtc: p.ConsumedAtUtc,
            ConsumedByTelegramUserId: p.ConsumedByTelegramUserId,
            ConsumedMessageId: p.ConsumedMessageId,
            RevokedBy: p.RevokedBy,
            RevokedAtUtc: p.RevokedAtUtc,
            RevocationReason: p.RevocationReason);
}
