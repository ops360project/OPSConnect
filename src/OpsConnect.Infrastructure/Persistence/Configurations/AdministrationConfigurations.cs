using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Common;

namespace OpsConnect.Infrastructure.Persistence.Configurations;

public class UserMasterConfiguration : IEntityTypeConfiguration<UserMaster>
{
    public void Configure(EntityTypeBuilder<UserMaster> builder)
    {
        builder.ToTable("UserMaster", DatabaseSchemas.Administration);

        builder.HasKey(x => x.UserId);
        builder.Property(x => x.Username).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(200).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(150).IsRequired();
        builder.Property(x => x.PasswordHash).HasMaxLength(500).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);

        builder.HasIndex(x => x.Username)
            .IsUnique()
            .HasDatabaseName("UX_UserMaster_Username");

        builder.HasIndex(x => x.Email)
            .IsUnique()
            .HasDatabaseName("UX_UserMaster_Email");
    }
}

public class RoleMasterConfiguration : IEntityTypeConfiguration<RoleMaster>
{
    private static readonly DateTime SeedCreatedAtUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public void Configure(EntityTypeBuilder<RoleMaster> builder)
    {
        builder.ToTable("RoleMaster", DatabaseSchemas.Administration);

        builder.HasKey(x => x.RoleId);
        builder.Property(x => x.RoleName).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(250);
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);

        builder.HasIndex(x => x.RoleName)
            .IsUnique()
            .HasDatabaseName("UX_RoleMaster_RoleName");

        builder.HasData(
            new RoleMaster
            {
                RoleId = 1,
                RoleName = SystemRoles.SuperAdmin,
                Description = "Full administrative control over OpsConnect configuration, users, and operations.",
                IsActive = true,
                CreatedAtUtc = SeedCreatedAtUtc,
                CreatedBy = "SystemSeed"
            },
            new RoleMaster
            {
                RoleId = 2,
                RoleName = SystemRoles.Admin,
                Description = "Operational administrator authorised to manage schedules, branches, and status reopening.",
                IsActive = true,
                CreatedAtUtc = SeedCreatedAtUtc,
                CreatedBy = "SystemSeed"
            },
            new RoleMaster
            {
                RoleId = 3,
                RoleName = SystemRoles.Viewer,
                Description = "Read-only access to dashboards, summaries, and audit logs.",
                IsActive = true,
                CreatedAtUtc = SeedCreatedAtUtc,
                CreatedBy = "SystemSeed"
            });
    }
}

public class UserRoleMappingConfiguration : IEntityTypeConfiguration<UserRoleMapping>
{
    public void Configure(EntityTypeBuilder<UserRoleMapping> builder)
    {
        builder.ToTable("UserRoleMapping", DatabaseSchemas.Administration);

        builder.HasKey(x => x.UserRoleMappingId);
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);

        builder.HasIndex(x => new { x.UserId, x.RoleId })
            .IsUnique()
            .HasDatabaseName("UX_UserRoleMapping_UserId_RoleId");

        builder.HasOne(x => x.User)
            .WithMany(u => u.UserRoles)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Role)
            .WithMany(r => r.UserRoles)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class SystemConfigurationConfiguration : IEntityTypeConfiguration<SystemConfiguration>
{
    public void Configure(EntityTypeBuilder<SystemConfiguration> builder)
    {
        builder.ToTable("SystemConfiguration", DatabaseSchemas.Administration);

        builder.HasKey(x => x.SystemConfigurationId);
        builder.Property(x => x.ConfigKey).HasMaxLength(100).IsRequired();
        builder.Property(x => x.ConfigValue).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);

        builder.HasIndex(x => x.ConfigKey)
            .IsUnique()
            .HasDatabaseName("UX_SystemConfiguration_ConfigKey");
    }
}

public class ActivityConfigurationEntityConfiguration : IEntityTypeConfiguration<ActivityConfiguration>
{
    public void Configure(EntityTypeBuilder<ActivityConfiguration> builder)
    {
        builder.ToTable("ActivityConfiguration", DatabaseSchemas.Administration);

        builder.HasKey(x => x.ActivityConfigurationId);
        builder.Property(x => x.ConfigKey).HasMaxLength(100).IsRequired();
        builder.Property(x => x.ConfigValue).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);

        builder.HasIndex(x => new { x.ActivityId, x.ConfigKey })
            .IsUnique()
            .HasDatabaseName("UX_ActivityConfiguration_ActivityId_ConfigKey");

        builder.HasOne(x => x.Activity)
            .WithMany()
            .HasForeignKey(x => x.ActivityId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class ActivityReportingConfigurationEntityConfiguration : IEntityTypeConfiguration<ActivityReportingConfiguration>
{
    private static readonly DateTime SeedCreatedAtUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public void Configure(EntityTypeBuilder<ActivityReportingConfiguration> builder)
    {
        builder.ToTable("ActivityReportingConfiguration", DatabaseSchemas.Administration);

        builder.HasKey(x => x.ConfigurationId);
        builder.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);

        builder.HasIndex(x => new { x.ActivityId, x.EffectiveFrom, x.EffectiveTo })
            .HasDatabaseName("IX_ActivityReportingConfiguration_Activity_Effective");

        builder.HasOne(x => x.Activity)
            .WithMany()
            .HasForeignKey(x => x.ActivityId)
            .OnDelete(DeleteBehavior.Restrict);

        // Initial RFFO sample configuration per Section 12 specification:
        // Start: 09:00, End: 18:00, Interval: 30 minutes, TimeZone: Asia/Kolkata, Enabled: Yes, StopWhenAllCompleted: Yes
        builder.HasData(new ActivityReportingConfiguration
        {
            ConfigurationId = 1,
            ActivityId = 1,
            ReportingStartTime = new TimeOnly(9, 0),
            ReportingEndTime = new TimeOnly(18, 0),
            ReportingIntervalMinutes = 30,
            TimeZoneId = "Asia/Kolkata",
            IsReportingEnabled = true,
            StopWhenAllCompleted = true,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            EffectiveTo = null,
            CreatedAtUtc = SeedCreatedAtUtc,
            CreatedBy = "SystemSeed"
        });
    }
}

public class ConfigurationHistoryConfiguration : IEntityTypeConfiguration<ConfigurationHistory>
{
    public void Configure(EntityTypeBuilder<ConfigurationHistory> builder)
    {
        builder.ToTable("ConfigurationHistory", DatabaseSchemas.Administration);

        builder.HasKey(x => x.ConfigurationHistoryId);
        builder.Property(x => x.EntityName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.EntityKey).HasMaxLength(100).IsRequired();
        builder.Property(x => x.ActionType).HasMaxLength(50).IsRequired();
        builder.Property(x => x.ChangeReason).HasMaxLength(500);
        builder.Property(x => x.ChangedBy).HasMaxLength(150).IsRequired();

        builder.HasIndex(x => new { x.EntityName, x.ChangedAtUtc })
            .HasDatabaseName("IX_ConfigurationHistory_Entity_ChangedAtUtc");
    }
}
