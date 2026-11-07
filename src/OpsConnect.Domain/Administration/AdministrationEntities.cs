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

/// <summary>
/// Configurable reporting schedule for an operational activity (e.g., RFFO: 09:00 to 18:00, 30 min interval, Asia/Kolkata).
/// </summary>
public class ActivityReportingConfiguration : AuditableEntity
{
    public int ConfigurationId { get; set; }
    public int ActivityId { get; set; }

    public TimeOnly ReportingStartTime { get; set; } = new(9, 0);
    public TimeOnly ReportingEndTime { get; set; } = new(18, 0);
    public int ReportingIntervalMinutes { get; set; } = 30;
    public string TimeZoneId { get; set; } = "Asia/Kolkata";

    public bool IsReportingEnabled { get; set; } = true;
    public bool StopWhenAllCompleted { get; set; } = true;

    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }

    public ActivityMaster Activity { get; set; } = null!;
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
