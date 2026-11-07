using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Common;

namespace OpsConnect.Infrastructure.Persistence.Configurations;

public class ActivityMasterConfiguration : IEntityTypeConfiguration<ActivityMaster>
{
    private static readonly DateTime SeedCreatedAtUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public void Configure(EntityTypeBuilder<ActivityMaster> builder)
    {
        builder.ToTable("ActivityMaster", DatabaseSchemas.Activity);

        builder.HasKey(x => x.ActivityId);
        builder.Property(x => x.ActivityCode).HasMaxLength(50).IsRequired();
        builder.Property(x => x.ActivityName).HasMaxLength(150).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500);
        builder.Property(x => x.ConfigurationJson).HasMaxLength(4000);
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);

        builder.HasIndex(x => x.ActivityCode)
            .IsUnique()
            .HasDatabaseName("UX_ActivityMaster_ActivityCode");

        builder.HasData(new ActivityMaster
        {
            ActivityId = 1,
            ActivityCode = "RFFO",
            ActivityName = "Request for Field Force Operations",
            Description = "Daily branch operational status monitoring and reporting for RFFO.",
            IsActive = true,
            CreatedAtUtc = SeedCreatedAtUtc,
            CreatedBy = "SystemSeed"
        });
    }
}

public class ActivityStatusConfiguration : IEntityTypeConfiguration<ActivityStatus>
{
    public void Configure(EntityTypeBuilder<ActivityStatus> builder)
    {
        builder.ToTable("ActivityStatus", DatabaseSchemas.Activity);

        builder.HasKey(x => x.ActivityStatusId);
        builder.Property(x => x.CurrentStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.LastUpdatedByDisplayName).HasMaxLength(200);
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.ActivityId, x.BranchId, x.ReportingDate })
            .IsUnique()
            .HasDatabaseName("UX_ActivityStatus_Activity_Branch_Date");

        builder.HasIndex(x => new { x.ActivityId, x.ReportingDate, x.CurrentStatus })
            .HasDatabaseName("IX_ActivityStatus_Activity_Date_Status");

        builder.HasOne(x => x.Activity)
            .WithMany(a => a.ActivityStatuses)
            .HasForeignKey(x => x.ActivityId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Branch)
            .WithMany()
            .HasForeignKey(x => x.BranchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class ActivityHistoryConfiguration : IEntityTypeConfiguration<ActivityHistory>
{
    public void Configure(EntityTypeBuilder<ActivityHistory> builder)
    {
        builder.ToTable("ActivityHistory", DatabaseSchemas.Activity);

        builder.HasKey(x => x.ActivityHistoryId);
        builder.Property(x => x.PreviousStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.NewStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.TelegramUsername).HasMaxLength(150);
        builder.Property(x => x.TelegramDisplayName).HasMaxLength(200);
        builder.Property(x => x.Notes).HasMaxLength(1000);

        builder.HasIndex(x => new { x.ActivityId, x.BranchId, x.ReportingDate, x.RecordedAtUtc })
            .HasDatabaseName("IX_ActivityHistory_Activity_Branch_Date");

        builder.HasOne(x => x.Activity)
            .WithMany(a => a.ActivityHistories)
            .HasForeignKey(x => x.ActivityId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Branch)
            .WithMany()
            .HasForeignKey(x => x.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.ConsumedReopenPermission)
            .WithMany()
            .HasForeignKey(x => x.ConsumedReopenPermissionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class StatusReopenPermissionConfiguration : IEntityTypeConfiguration<StatusReopenPermission>
{
    public void Configure(EntityTypeBuilder<StatusReopenPermission> builder)
    {
        builder.ToTable("StatusReopenPermission", DatabaseSchemas.Activity);

        builder.HasKey(x => x.PermissionId);
        builder.Property(x => x.EnabledBy).HasMaxLength(150).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        builder.Property(x => x.RevokedBy).HasMaxLength(150);
        builder.Property(x => x.RevocationReason).HasMaxLength(500);
        builder.Property(x => x.PermissionStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.BranchId, x.ActivityId, x.ReportingDate, x.PermissionStatus })
            .HasDatabaseName("IX_StatusReopenPermission_Lookup");

        // Filtered unique index ensuring at most one Available reopening permission exists per Branch + Activity + Date,
        // while allowing multiple historical Consumed/Expired permissions over time.
        builder.HasIndex(x => new { x.BranchId, x.ActivityId, x.ReportingDate })
            .IsUnique()
            .HasFilter("[PermissionStatus] = 'Available'")
            .HasDatabaseName("UX_StatusReopenPermission_ActivePerBranchDate");

        builder.HasOne(x => x.Branch)
            .WithMany()
            .HasForeignKey(x => x.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Activity)
            .WithMany(a => a.ReopenPermissions)
            .HasForeignKey(x => x.ActivityId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
