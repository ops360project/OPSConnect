namespace OpsConnect.Domain.Common;

/// <summary>
/// Canonical SQL Server schema names used across OpsConnectDB.
/// </summary>
public static class DatabaseSchemas
{
    public const string Organisation = "org";
    public const string Activity = "activity";
    public const string Telegram = "telegram";
    public const string Reporting = "reporting";
    public const string Administration = "admin";
}

/// <summary>
/// Base class for entities requiring standard UTC creation and modification audit fields.
/// </summary>
public abstract class AuditableEntity
{
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// Base class for master entities supporting active/inactive lifecycle state alongside UTC audit fields.
/// </summary>
public abstract class ActivatableAuditableEntity : AuditableEntity
{
    public bool IsActive { get; set; } = true;
}
