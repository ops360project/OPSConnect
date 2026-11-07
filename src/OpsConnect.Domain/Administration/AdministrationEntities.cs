using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Common;

namespace OpsConnect.Domain.Administration;

public static class SystemRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    public const string Viewer = "Viewer";
}

public class UserMaster : ActivatableAuditableEntity
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime? LastLoginAtUtc { get; set; }

    public ICollection<UserRoleMapping> UserRoles { get; set; } = new List<UserRoleMapping>();
}

public class RoleMaster : ActivatableAuditableEntity
{
    public int RoleId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public string? Description { get; set; }

    public ICollection<UserRoleMapping> UserRoles { get; set; } = new List<UserRoleMapping>();
}

public class UserRoleMapping : AuditableEntity
{
    public int UserRoleMappingId { get; set; }
    public int UserId { get; set; }
    public int RoleId { get; set; }

    public UserMaster User { get; set; } = null!;
    public RoleMaster Role { get; set; } = null!;
}

public class SystemConfiguration : AuditableEntity
{
    public int SystemConfigurationId { get; set; }
    public string ConfigKey { get; set; } = string.Empty;
    public string ConfigValue { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class ActivityConfiguration : AuditableEntity
{
    public int ActivityConfigurationId { get; set; }
    public int ActivityId { get; set; }
    public string ConfigKey { get; set; } = string.Empty;
    public string ConfigValue { get; set; } = string.Empty;
    public string? Description { get; set; }

    public ActivityMaster Activity { get; set; } = null!;
}

public sealed record ScheduledSlotInstance(
    TimeOnly SlotTime,
    DateTime ScheduledExecutionUtc,
    bool WasAdjustedForDstGap,
    bool WasAmbiguousDstTime);

/// <summary>
/// Configurable reporting schedule for an operational activity (e.g., RFFO: 09:00 to 18:00, 30 min interval, Asia/Kolkata).
/// </summary>
public class ActivityReportingConfiguration : AuditableEntity
{
    public const string DefaultTimeZoneId = "Asia/Kolkata";
    public const int MaxIntervalMinutes = 1440;

    public int ConfigurationId { get; set; }
    public int ActivityId { get; set; }

    public TimeOnly ReportingStartTime { get; set; } = new(9, 0);
    public TimeOnly ReportingEndTime { get; set; } = new(18, 0);
    public int ReportingIntervalMinutes { get; set; } = 30;
    public string TimeZoneId { get; set; } = DefaultTimeZoneId;

    public bool IsReportingEnabled { get; set; } = true;
    public bool StopWhenAllCompleted { get; set; } = true;

    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public ActivityMaster Activity { get; set; } = null!;

    public static ActivityReportingConfiguration Create(
        int activityId,
        TimeOnly reportingStartTime,
        TimeOnly reportingEndTime,
        int reportingIntervalMinutes,
        string timeZoneId,
        DateOnly effectiveFrom,
        bool isReportingEnabled = true,
        bool stopWhenAllCompleted = true,
        DateOnly? effectiveTo = null,
        string? createdBy = null,
        DateTime? createdAtUtc = null,
        IEnumerable<ActivityReportingConfiguration>? existingConfigurations = null)
    {
        if (activityId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(activityId), "ActivityId must be positive.");
        }

        ValidateSchedule(
            reportingStartTime,
            reportingEndTime,
            reportingIntervalMinutes,
            timeZoneId,
            effectiveFrom,
            effectiveTo);

        if (existingConfigurations is not null)
        {
            EnsureNoOverlap(
                activityId,
                effectiveFrom,
                effectiveTo,
                existingConfigurations,
                excludeConfigurationId: null);
        }

        return new ActivityReportingConfiguration
        {
            ActivityId = activityId,
            ReportingStartTime = reportingStartTime,
            ReportingEndTime = reportingEndTime,
            ReportingIntervalMinutes = reportingIntervalMinutes,
            TimeZoneId = timeZoneId.Trim(),
            IsReportingEnabled = isReportingEnabled,
            StopWhenAllCompleted = stopWhenAllCompleted,
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim(),
            CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow
        };
    }

    public void UpdateDetails(
        TimeOnly reportingStartTime,
        TimeOnly reportingEndTime,
        int reportingIntervalMinutes,
        string timeZoneId,
        bool isReportingEnabled,
        bool stopWhenAllCompleted,
        DateOnly effectiveFrom,
        DateOnly? effectiveTo = null,
        string? updatedBy = null,
        DateTime? updatedAtUtc = null,
        IEnumerable<ActivityReportingConfiguration>? existingConfigurations = null)
    {
        ValidateSchedule(
            reportingStartTime,
            reportingEndTime,
            reportingIntervalMinutes,
            timeZoneId,
            effectiveFrom,
            effectiveTo);

        if (existingConfigurations is not null)
        {
            EnsureNoOverlap(
                ActivityId,
                effectiveFrom,
                effectiveTo,
                existingConfigurations,
                excludeConfigurationId: ConfigurationId > 0 ? ConfigurationId : null);
        }

        ReportingStartTime = reportingStartTime;
        ReportingEndTime = reportingEndTime;
        ReportingIntervalMinutes = reportingIntervalMinutes;
        TimeZoneId = timeZoneId.Trim();
        IsReportingEnabled = isReportingEnabled;
        StopWhenAllCompleted = stopWhenAllCompleted;
        EffectiveFrom = effectiveFrom;
        EffectiveTo = effectiveTo;
        UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy.Trim();
        UpdatedAtUtc = updatedAtUtc ?? DateTime.UtcNow;
    }

    public void CloseEffectivePeriod(
        DateOnly effectiveTo,
        string? updatedBy = null,
        DateTime? updatedAtUtc = null)
    {
        if (effectiveTo < EffectiveFrom)
        {
            throw new InvalidOperationException(
                $"EffectiveTo ({effectiveTo:yyyy-MM-dd}) cannot be earlier than EffectiveFrom ({EffectiveFrom:yyyy-MM-dd}).");
        }

        EffectiveTo = effectiveTo;
        UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy.Trim();
        UpdatedAtUtc = updatedAtUtc ?? DateTime.UtcNow;
    }

    public static void ValidateSchedule(
        TimeOnly reportingStartTime,
        TimeOnly reportingEndTime,
        int reportingIntervalMinutes,
        string? timeZoneId,
        DateOnly effectiveFrom = default,
        DateOnly? effectiveTo = null)
    {
        if (reportingEndTime <= reportingStartTime)
        {
            throw new ArgumentException(
                $"ReportingEndTime ({reportingEndTime:HH\\:mm}) must be later than ReportingStartTime ({reportingStartTime:HH\\:mm}).",
                nameof(reportingEndTime));
        }

        if (reportingIntervalMinutes <= 0 || reportingIntervalMinutes > MaxIntervalMinutes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reportingIntervalMinutes),
                $"ReportingIntervalMinutes ({reportingIntervalMinutes}) must be between 1 and {MaxIntervalMinutes} minutes.");
        }

        _ = ResolveValidatedTimeZone(timeZoneId);

        if (effectiveTo.HasValue && effectiveTo.Value < effectiveFrom)
        {
            throw new ArgumentException(
                $"EffectiveTo ({effectiveTo.Value:yyyy-MM-dd}) cannot be earlier than EffectiveFrom ({effectiveFrom:yyyy-MM-dd}).",
                nameof(effectiveTo));
        }
    }

    public static void EnsureNoOverlap(
        int activityId,
        DateOnly effectiveFrom,
        DateOnly? effectiveTo,
        IEnumerable<ActivityReportingConfiguration> existingConfigurations,
        int? excludeConfigurationId = null)
    {
        ArgumentNullException.ThrowIfNull(existingConfigurations);

        if (effectiveTo.HasValue && effectiveTo.Value < effectiveFrom)
        {
            throw new ArgumentException(
                $"EffectiveTo ({effectiveTo.Value:yyyy-MM-dd}) cannot be earlier than EffectiveFrom ({effectiveFrom:yyyy-MM-dd}).",
                nameof(effectiveTo));
        }

        var candidateEnd = effectiveTo ?? DateOnly.MaxValue;

        foreach (var existing in existingConfigurations)
        {
            if (existing.ActivityId != activityId)
            {
                continue;
            }

            if (excludeConfigurationId.HasValue && existing.ConfigurationId == excludeConfigurationId.Value)
            {
                continue;
            }

            var existingEnd = existing.EffectiveTo ?? DateOnly.MaxValue;
            bool overlaps = effectiveFrom <= existingEnd && existing.EffectiveFrom <= candidateEnd;
            if (overlaps)
            {
                var existingRangeText = existing.EffectiveTo.HasValue
                    ? $"{existing.EffectiveFrom:yyyy-MM-dd}..{existing.EffectiveTo.Value:yyyy-MM-dd}"
                    : $"{existing.EffectiveFrom:yyyy-MM-dd}..open";
                var candidateRangeText = effectiveTo.HasValue
                    ? $"{effectiveFrom:yyyy-MM-dd}..{effectiveTo.Value:yyyy-MM-dd}"
                    : $"{effectiveFrom:yyyy-MM-dd}..open";

                throw new InvalidOperationException(
                    $"Reporting configuration effective period ({candidateRangeText}) for ActivityId {activityId} overlaps with existing period ({existingRangeText}).");
            }
        }
    }

    public static TimeZoneInfo ResolveValidatedTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new ArgumentException("TimeZoneId is required and cannot be empty or whitespace.", nameof(timeZoneId));
        }

        var trimmed = timeZoneId.Trim();
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(trimmed);
        }
        catch (TimeZoneNotFoundException) when (string.Equals(trimmed, DefaultTimeZoneId, StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException($"TimeZoneId '{timeZoneId}' is not a valid system or IANA time zone identifier.", nameof(timeZoneId), ex);
        }
    }

    /// <summary>
    /// Dynamically calculates the ordered local slot times for the configured window.
    /// Includes ReportingEndTime when it aligns with the configured interval, and stops at the last
    /// in-window slot when the interval does not divide the window evenly.
    /// </summary>
    public IReadOnlyList<TimeOnly> CalculateDailySlotTimes()
    {
        ValidateSchedule(
            ReportingStartTime,
            ReportingEndTime,
            ReportingIntervalMinutes,
            TimeZoneId,
            EffectiveFrom,
            EffectiveTo);

        var slots = new List<TimeOnly>();
        var currentSpan = ReportingStartTime.ToTimeSpan();
        var endSpan = ReportingEndTime.ToTimeSpan();
        var stepSpan = TimeSpan.FromMinutes(ReportingIntervalMinutes);

        while (currentSpan <= endSpan)
        {
            slots.Add(TimeOnly.FromTimeSpan(currentSpan));
            currentSpan += stepSpan;
        }

        return slots;
    }

    /// <summary>
    /// Calculates the scheduled slot instances (including deterministic UTC execution times)
    /// for a specific reporting date. Returns an empty list if reporting is disabled or if the
    /// reporting date falls outside [EffectiveFrom, EffectiveTo].
    /// </summary>
    public IReadOnlyList<ScheduledSlotInstance> CalculateDailySlotsWithUtc(DateOnly reportingDate)
    {
        var timeZone = ResolveValidatedTimeZone(TimeZoneId);
        var localSlots = CalculateDailySlotTimes();

        if (!IsReportingEnabled || !IsEffectiveOn(reportingDate))
        {
            return Array.Empty<ScheduledSlotInstance>();
        }

        var instances = new List<ScheduledSlotInstance>(localSlots.Count);
        foreach (var slotTime in localSlots)
        {
            var utc = ConvertSlotTimeToUtc(
                reportingDate,
                slotTime,
                timeZone,
                out var wasAdjustedForDstGap,
                out var wasAmbiguousDstTime);

            instances.Add(new ScheduledSlotInstance(
                SlotTime: slotTime,
                ScheduledExecutionUtc: utc,
                WasAdjustedForDstGap: wasAdjustedForDstGap,
                WasAmbiguousDstTime: wasAmbiguousDstTime));
        }

        return instances;
    }

    public bool IsEffectiveOn(DateOnly reportingDate) =>
        reportingDate >= EffectiveFrom &&
        (!EffectiveTo.HasValue || reportingDate <= EffectiveTo.Value);

    /// <summary>
    /// Converts a local (reportingDate, slotTime) pair in the specified time zone to UTC with
    /// explicit, deterministic handling of Daylight Saving Time (DST) transitions:
    /// - Ambiguous local times (fall-back overlap) resolve to the first chronological occurrence (daylight saving offset).
    /// - Invalid local times (spring-forward gap) shift forward by the DST gap duration so slot spacing is preserved.
    /// </summary>
    public static DateTime ConvertSlotTimeToUtc(
        DateOnly reportingDate,
        TimeOnly slotTime,
        TimeZoneInfo timeZone,
        out bool wasAdjustedForDstGap,
        out bool wasAmbiguousDstTime)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var localUnspecified = reportingDate.ToDateTime(slotTime, DateTimeKind.Unspecified);

        if (timeZone.IsAmbiguousTime(localUnspecified))
        {
            wasAdjustedForDstGap = false;
            wasAmbiguousDstTime = true;
            var offsets = timeZone.GetAmbiguousTimeOffsets(localUnspecified);
            var firstOccurrenceOffset = offsets.Max();
            return new DateTimeOffset(localUnspecified, firstOccurrenceOffset).UtcDateTime;
        }

        if (timeZone.IsInvalidTime(localUnspecified))
        {
            wasAdjustedForDstGap = true;
            wasAmbiguousDstTime = false;

            // Find the start and end of the invalid DST spring-forward window to compute exact gap duration
            var probeStart = localUnspecified;
            while (timeZone.IsInvalidTime(probeStart.AddMinutes(-1)))
            {
                probeStart = probeStart.AddMinutes(-1);
            }

            var probeEnd = localUnspecified;
            while (timeZone.IsInvalidTime(probeEnd))
            {
                probeEnd = probeEnd.AddMinutes(1);
            }

            var gapDuration = probeEnd - probeStart;
            var adjustedLocal = localUnspecified.Add(gapDuration);
            return TimeZoneInfo.ConvertTimeToUtc(adjustedLocal, timeZone);
        }

        wasAdjustedForDstGap = false;
        wasAmbiguousDstTime = false;
        return TimeZoneInfo.ConvertTimeToUtc(localUnspecified, timeZone);
    }
}

/// <summary>
/// Immutable audit history of administrative configuration changes.
/// </summary>
public class ConfigurationHistory
{
    public long ConfigurationHistoryId { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public string EntityKey { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public string? PreviousValueJson { get; set; }
    public string? NewValueJson { get; set; }
    public string? ChangeReason { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
    public DateTime ChangedAtUtc { get; set; } = DateTime.UtcNow;
}
