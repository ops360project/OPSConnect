using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpsConnect.Application.Administration;
using OpsConnect.Application.Common;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using OpsConnect.Infrastructure.Persistence;

namespace OpsConnect.Infrastructure.Services;

public sealed class ActivityConfigurationService : IActivityConfigurationService
{
    private readonly OpsConnectDbContext _dbContext;
    private readonly IBusinessClock _businessClock;

    public ActivityConfigurationService(
        OpsConnectDbContext dbContext,
        IBusinessClock businessClock)
    {
        _dbContext = dbContext;
        _businessClock = businessClock;
    }

    public async Task<IReadOnlyList<ActivitySummaryDto>> GetActivitiesAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Activities.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(a => a.IsActive);
        }

        var activities = await query
            .OrderBy(a => a.ActivityCode)
            .ToListAsync(cancellationToken);

        var openScheduleActivityIds = (await _dbContext.ActivityReportingConfigurations
            .AsNoTracking()
            .Where(c => c.EffectiveTo == null && c.IsReportingEnabled)
            .Select(c => c.ActivityId)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        return activities
            .Select(a => new ActivitySummaryDto(
                ActivityId: a.ActivityId,
                ActivityCode: a.ActivityCode,
                ActivityName: a.ActivityName,
                Description: a.Description,
                IsActive: a.IsActive,
                HasActiveReportingConfiguration: openScheduleActivityIds.Contains(a.ActivityId),
                CreatedAtUtc: a.CreatedAtUtc,
                UpdatedAtUtc: a.UpdatedAtUtc))
            .ToList();
    }

    public async Task<ActivityDetailsDto?> GetActivityByCodeAsync(
        string activityCode,
        DateOnly? asOfReportingDate = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(activityCode))
        {
            return null;
        }

        string normalizedCode;
        try
        {
            normalizedCode = ActivityMaster.ValidateActivityCode(activityCode);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var activity = await _dbContext.Activities
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.ActivityCode == normalizedCode, cancellationToken);

        if (activity is null)
        {
            return null;
        }

        return await BuildActivityDetailsDtoAsync(activity, asOfReportingDate, cancellationToken);
    }

    public async Task<ActivityConfigurationOperationResult> UpsertActivityAsync(
        UpsertActivityMasterRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = request.ValidateAndNormalize();

        return await ExecuteInTransactionAsync(async () =>
        {
            var nowUtc = _businessClock.UtcNow;
            var existing = await _dbContext.Activities
                .SingleOrDefaultAsync(a => a.ActivityCode == normalized.ActivityCode, cancellationToken);

            string actionType;
            string? previousJson = null;
            ActivityMaster target;

            if (existing is null)
            {
                actionType = "Create";
                target = ActivityMaster.Create(
                    activityCode: normalized.ActivityCode,
                    activityName: normalized.ActivityName,
                    description: normalized.Description,
                    configurationJson: normalized.ConfigurationJson,
                    isActive: normalized.IsActive,
                    createdBy: normalized.ChangedBy,
                    createdAtUtc: nowUtc);

                _dbContext.Activities.Add(target);
            }
            else
            {
                previousJson = SerializeActivitySnapshot(existing);
                bool activeStateChanged = existing.IsActive != normalized.IsActive;
                actionType = activeStateChanged
                    ? (normalized.IsActive ? "Enable" : "Disable")
                    : "Update";

                existing.UpdateDetails(
                    activityCode: normalized.ActivityCode,
                    activityName: normalized.ActivityName,
                    description: normalized.Description,
                    configurationJson: normalized.ConfigurationJson,
                    isActive: normalized.IsActive,
                    updatedBy: normalized.ChangedBy,
                    updatedAtUtc: nowUtc);

                target = existing;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            var auditEntry = new ConfigurationHistory
            {
                EntityName = nameof(ActivityMaster),
                EntityKey = target.ActivityCode,
                ActionType = actionType,
                PreviousValueJson = previousJson,
                NewValueJson = SerializeActivitySnapshot(target),
                ChangeReason = normalized.ChangeReason ?? $"{actionType} activity {target.ActivityCode}",
                ChangedBy = normalized.ChangedBy,
                ChangedAtUtc = nowUtc
            };

            _dbContext.ConfigurationHistories.Add(auditEntry);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var detailsDto = await BuildActivityDetailsDtoAsync(target, null, cancellationToken);
            return new ActivityConfigurationOperationResult(
                IsSuccess: true,
                ErrorCode: null,
                ErrorMessage: null,
                Activity: detailsDto);
        }, cancellationToken);
    }

    public async Task<ActivityConfigurationOperationResult> SetActivityConfigurationValueAsync(
        SetActivityKeyValueConfigRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = request.ValidateAndNormalize();

        return await ExecuteInTransactionAsync(async () =>
        {
            var activity = await _dbContext.Activities
                .AsNoTracking()
                .SingleOrDefaultAsync(a => a.ActivityCode == normalized.ActivityCode, cancellationToken);

            if (activity is null)
            {
                return new ActivityConfigurationOperationResult(
                    IsSuccess: false,
                    ErrorCode: "ActivityNotFound",
                    ErrorMessage: $"Activity '{normalized.ActivityCode}' was not found.",
                    Activity: null);
            }

            var nowUtc = _businessClock.UtcNow;
            var existingSetting = await _dbContext.ActivityConfigurations
                .SingleOrDefaultAsync(
                    c => c.ActivityId == activity.ActivityId && c.ConfigKey == normalized.ConfigKey,
                    cancellationToken);

            string actionType;
            string? previousJson = null;
            ActivityConfiguration targetSetting;

            if (existingSetting is null)
            {
                actionType = "Create";
                targetSetting = new ActivityConfiguration
                {
                    ActivityId = activity.ActivityId,
                    ConfigKey = normalized.ConfigKey,
                    ConfigValue = normalized.ConfigValue,
                    Description = normalized.Description,
                    CreatedBy = normalized.ChangedBy,
                    CreatedAtUtc = nowUtc
                };

                _dbContext.ActivityConfigurations.Add(targetSetting);
            }
            else
            {
                actionType = "Update";
                previousJson = SerializeKeyValueSnapshot(existingSetting);
                existingSetting.ConfigValue = normalized.ConfigValue;
                existingSetting.Description = normalized.Description;
                existingSetting.UpdatedBy = normalized.ChangedBy;
                existingSetting.UpdatedAtUtc = nowUtc;
                targetSetting = existingSetting;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            var auditEntry = new ConfigurationHistory
            {
                EntityName = nameof(ActivityConfiguration),
                EntityKey = $"{activity.ActivityCode}:{normalized.ConfigKey}",
                ActionType = actionType,
                PreviousValueJson = previousJson,
                NewValueJson = SerializeKeyValueSnapshot(targetSetting),
                ChangeReason = normalized.ChangeReason ?? $"{actionType} setting {normalized.ConfigKey}",
                ChangedBy = normalized.ChangedBy,
                ChangedAtUtc = nowUtc
            };

            _dbContext.ConfigurationHistories.Add(auditEntry);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var detailsDto = await BuildActivityDetailsDtoAsync(activity, null, cancellationToken);
            return new ActivityConfigurationOperationResult(
                IsSuccess: true,
                ErrorCode: null,
                ErrorMessage: null,
                Activity: detailsDto);
        }, cancellationToken);
    }

    public async Task<ActivityReportingScheduleResultDto?> GetReportingConfigurationAsync(
        string activityCode,
        DateOnly? asOfReportingDate = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(activityCode))
        {
            return null;
        }

        string normalizedCode;
        try
        {
            normalizedCode = ActivityMaster.ValidateActivityCode(activityCode);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var activity = await _dbContext.Activities
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.ActivityCode == normalizedCode, cancellationToken);

        if (activity is null)
        {
            return null;
        }

        var configs = await _dbContext.ActivityReportingConfigurations
            .AsNoTracking()
            .Where(c => c.ActivityId == activity.ActivityId)
            .OrderByDescending(c => c.EffectiveFrom)
            .ThenByDescending(c => c.ConfigurationId)
            .ToListAsync(cancellationToken);

        var defaultTz = configs.FirstOrDefault(c => c.EffectiveTo == null)?.TimeZoneId
            ?? configs.FirstOrDefault()?.TimeZoneId
            ?? ActivityReportingConfiguration.DefaultTimeZoneId;

        var effectiveDate = asOfReportingDate ?? _businessClock.GetBusinessDate(defaultTz);
        var resolvedEntity = configs.FirstOrDefault(c => c.IsEffectiveOn(effectiveDate));

        var historyDtos = configs
            .Select(c => MapReportingConfigToDto(c, activity.ActivityCode))
            .ToList();

        return new ActivityReportingScheduleResultDto(
            ActivityId: activity.ActivityId,
            ActivityCode: activity.ActivityCode,
            ActivityName: activity.ActivityName,
            IsActivityActive: activity.IsActive,
            AsOfReportingDate: effectiveDate,
            ResolvedConfigurationAsOfDate: resolvedEntity is null
                ? null
                : MapReportingConfigToDto(resolvedEntity, activity.ActivityCode),
            ConfigurationHistory: historyDtos);
    }

    public async Task<ReportingConfigurationUpdateResult> UpdateReportingConfigurationAsync(
        UpdateActivityReportingConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = request.ValidateAndNormalize();

        return await ExecuteInTransactionAsync(async () =>
        {
            var activity = await _dbContext.Activities
                .AsNoTracking()
                .SingleOrDefaultAsync(a => a.ActivityCode == normalized.ActivityCode, cancellationToken);

            if (activity is null)
            {
                return new ReportingConfigurationUpdateResult(
                    IsSuccess: false,
                    ErrorCode: "ActivityNotFound",
                    ErrorMessage: $"Activity '{normalized.ActivityCode}' was not found.",
                    ActiveConfiguration: null,
                    AuditEntry: null);
            }

            var existingConfigs = await _dbContext.ActivityReportingConfigurations
                .Where(c => c.ActivityId == activity.ActivityId)
                .OrderBy(c => c.EffectiveFrom)
                .ToListAsync(cancellationToken);

            var nowUtc = _businessClock.UtcNow;
            var currentOpen = existingConfigs.SingleOrDefault(c => c.EffectiveTo == null);

            string actionType;
            string? previousJson = currentOpen is null ? null : SerializeReportingConfigSnapshot(currentOpen);
            ActivityReportingConfiguration activeEntity;

            try
            {
                // Case 1: Same-day update to the current open-ended configuration
                if (currentOpen is not null && currentOpen.EffectiveFrom == normalized.EffectiveFrom)
                {
                    actionType = "UpdateSameDay";
                    currentOpen.UpdateDetails(
                        reportingStartTime: normalized.ReportingStartTime,
                        reportingEndTime: normalized.ReportingEndTime,
                        reportingIntervalMinutes: normalized.ReportingIntervalMinutes,
                        timeZoneId: normalized.TimeZoneId,
                        isReportingEnabled: normalized.IsReportingEnabled,
                        stopWhenAllCompleted: normalized.StopWhenAllCompleted,
                        effectiveFrom: normalized.EffectiveFrom,
                        effectiveTo: normalized.EffectiveTo,
                        updatedBy: normalized.ChangedBy,
                        updatedAtUtc: nowUtc,
                        existingConfigurations: existingConfigs);

                    await _dbContext.SaveChangesAsync(cancellationToken);
                    activeEntity = currentOpen;
                }
                else
                {
                    // Case 2: If auto-closing is enabled and there is an earlier open-ended configuration,
                    // close it as of (EffectiveFrom - 1 day) before validating overlap and inserting the new configuration.
                    if (currentOpen is not null &&
                        normalized.AutoClosePreviousOpenPeriod &&
                        normalized.EffectiveFrom > currentOpen.EffectiveFrom)
                    {
                        var closedThroughDate = normalized.EffectiveFrom.AddDays(-1);

                        // Validate that the new range will not overlap any other closed configurations once currentOpen is closed
                        var simulatedConfigs = existingConfigs
                            .Where(c => c.ConfigurationId != currentOpen.ConfigurationId)
                            .ToList();

                        ActivityReportingConfiguration.EnsureNoOverlap(
                            activityId: activity.ActivityId,
                            effectiveFrom: normalized.EffectiveFrom,
                            effectiveTo: normalized.EffectiveTo,
                            existingConfigurations: simulatedConfigs);

                        currentOpen.CloseEffectivePeriod(closedThroughDate, normalized.ChangedBy, nowUtc);
                        // Flush the close update first so UX_ActivityReportingConfiguration_ActivePerActivity is satisfied
                        await _dbContext.SaveChangesAsync(cancellationToken);
                    }

                    actionType = currentOpen is null ? "Create" : "RolloverEffectivePeriod";
                    activeEntity = ActivityReportingConfiguration.Create(
                        activityId: activity.ActivityId,
                        reportingStartTime: normalized.ReportingStartTime,
                        reportingEndTime: normalized.ReportingEndTime,
                        reportingIntervalMinutes: normalized.ReportingIntervalMinutes,
                        timeZoneId: normalized.TimeZoneId,
                        effectiveFrom: normalized.EffectiveFrom,
                        isReportingEnabled: normalized.IsReportingEnabled,
                        stopWhenAllCompleted: normalized.StopWhenAllCompleted,
                        effectiveTo: normalized.EffectiveTo,
                        createdBy: normalized.ChangedBy,
                        createdAtUtc: nowUtc,
                        existingConfigurations: existingConfigs);

                    _dbContext.ActivityReportingConfigurations.Add(activeEntity);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
            }
            catch (InvalidOperationException ex)
            {
                return new ReportingConfigurationUpdateResult(
                    IsSuccess: false,
                    ErrorCode: "OverlappingEffectivePeriod",
                    ErrorMessage: ex.Message,
                    ActiveConfiguration: null,
                    AuditEntry: null);
            }

            var auditEntity = new ConfigurationHistory
            {
                EntityName = nameof(ActivityReportingConfiguration),
                EntityKey = activity.ActivityCode,
                ActionType = actionType,
                PreviousValueJson = previousJson,
                NewValueJson = SerializeReportingConfigSnapshot(activeEntity),
                ChangeReason = normalized.ChangeReason ?? $"{actionType} reporting schedule for {activity.ActivityCode}",
                ChangedBy = normalized.ChangedBy,
                ChangedAtUtc = nowUtc
            };

            _dbContext.ConfigurationHistories.Add(auditEntity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new ReportingConfigurationUpdateResult(
                IsSuccess: true,
                ErrorCode: null,
                ErrorMessage: null,
                ActiveConfiguration: MapReportingConfigToDto(activeEntity, activity.ActivityCode),
                AuditEntry: MapAuditEntryToDto(auditEntity));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ConfigurationHistoryEntryDto>> GetConfigurationHistoryAsync(
        string? entityName = null,
        string? entityKey = null,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.ConfigurationHistories.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(entityName))
        {
            var trimmedName = entityName.Trim();
            query = query.Where(h => h.EntityName == trimmedName);
        }

        if (!string.IsNullOrWhiteSpace(entityKey))
        {
            var trimmedKey = entityKey.Trim();
            query = query.Where(h => h.EntityKey == trimmedKey);
        }

        var entries = await query
            .OrderByDescending(h => h.ChangedAtUtc)
            .ThenByDescending(h => h.ConfigurationHistoryId)
            .ToListAsync(cancellationToken);

        return entries.Select(MapAuditEntryToDto).ToList();
    }

    private async Task<ActivityDetailsDto> BuildActivityDetailsDtoAsync(
        ActivityMaster activity,
        DateOnly? asOfReportingDate,
        CancellationToken cancellationToken)
    {
        var scheduleResult = await GetReportingConfigurationAsync(
            activity.ActivityCode,
            asOfReportingDate,
            cancellationToken);

        var settings = await _dbContext.ActivityConfigurations
            .AsNoTracking()
            .Where(s => s.ActivityId == activity.ActivityId)
            .OrderBy(s => s.ConfigKey)
            .Select(s => new ActivityKeyValueConfigDto(
                s.ActivityConfigurationId,
                s.ActivityId,
                s.ConfigKey,
                s.ConfigValue,
                s.Description,
                s.CreatedAtUtc,
                s.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        return new ActivityDetailsDto(
            ActivityId: activity.ActivityId,
            ActivityCode: activity.ActivityCode,
            ActivityName: activity.ActivityName,
            Description: activity.Description,
            ConfigurationJson: activity.ConfigurationJson,
            IsActive: activity.IsActive,
            EffectiveReportingConfiguration: scheduleResult?.ResolvedConfigurationAsOfDate,
            Settings: settings,
            CreatedAtUtc: activity.CreatedAtUtc,
            UpdatedAtUtc: activity.UpdatedAtUtc);
    }

    private async Task<T> ExecuteInTransactionAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var ambientTransaction = _dbContext.Database.CurrentTransaction;
        if (ambientTransaction is not null)
        {
            const string savepointName = "ActivityConfigSavepoint";
            await ambientTransaction.CreateSavepointAsync(savepointName, cancellationToken);
            try
            {
                var result = await operation();
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
                var result = await operation();
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

    private static ActivityReportingConfigurationDto MapReportingConfigToDto(
        ActivityReportingConfiguration c,
        string activityCode) =>
        new(
            ConfigurationId: c.ConfigurationId,
            ActivityId: c.ActivityId,
            ActivityCode: activityCode,
            ReportingStartTime: c.ReportingStartTime,
            ReportingEndTime: c.ReportingEndTime,
            ReportingIntervalMinutes: c.ReportingIntervalMinutes,
            TimeZoneId: c.TimeZoneId,
            IsReportingEnabled: c.IsReportingEnabled,
            StopWhenAllCompleted: c.StopWhenAllCompleted,
            EffectiveFrom: c.EffectiveFrom,
            EffectiveTo: c.EffectiveTo,
            IsCurrentOpenConfiguration: c.EffectiveTo == null,
            CalculatedDailySlotTimes: c.CalculateDailySlotTimes(),
            CreatedAtUtc: c.CreatedAtUtc,
            CreatedBy: c.CreatedBy,
            UpdatedAtUtc: c.UpdatedAtUtc,
            UpdatedBy: c.UpdatedBy);

    private static ConfigurationHistoryEntryDto MapAuditEntryToDto(ConfigurationHistory h) =>
        new(
            ConfigurationHistoryId: h.ConfigurationHistoryId,
            EntityName: h.EntityName,
            EntityKey: h.EntityKey,
            ActionType: h.ActionType,
            PreviousValueJson: h.PreviousValueJson,
            NewValueJson: h.NewValueJson,
            ChangeReason: h.ChangeReason,
            ChangedBy: h.ChangedBy,
            ChangedAtUtc: h.ChangedAtUtc);

    private static string SerializeActivitySnapshot(ActivityMaster a) =>
        JsonSerializer.Serialize(new
        {
            a.ActivityId,
            a.ActivityCode,
            a.ActivityName,
            a.Description,
            a.ConfigurationJson,
            a.IsActive
        });

    private static string SerializeKeyValueSnapshot(ActivityConfiguration c) =>
        JsonSerializer.Serialize(new
        {
            c.ActivityConfigurationId,
            c.ActivityId,
            c.ConfigKey,
            c.ConfigValue,
            c.Description
        });

    private static string SerializeReportingConfigSnapshot(ActivityReportingConfiguration c) =>
        JsonSerializer.Serialize(new
        {
            c.ConfigurationId,
            c.ActivityId,
            ReportingStartTime = c.ReportingStartTime.ToString("HH:mm"),
            ReportingEndTime = c.ReportingEndTime.ToString("HH:mm"),
            c.ReportingIntervalMinutes,
            c.TimeZoneId,
            c.IsReportingEnabled,
            c.StopWhenAllCompleted,
            EffectiveFrom = c.EffectiveFrom.ToString("yyyy-MM-dd"),
            EffectiveTo = c.EffectiveTo?.ToString("yyyy-MM-dd")
        });
}
