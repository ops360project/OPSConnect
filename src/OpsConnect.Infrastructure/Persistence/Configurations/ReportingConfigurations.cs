using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsConnect.Domain.Common;
using OpsConnect.Domain.Reporting;

namespace OpsConnect.Infrastructure.Persistence.Configurations;

public class ReportingCycleConfiguration : IEntityTypeConfiguration<ReportingCycle>
{
    public void Configure(EntityTypeBuilder<ReportingCycle> builder)
    {
        builder.ToTable("ReportingCycle", DatabaseSchemas.Reporting);

        builder.HasKey(x => x.ReportingCycleId);
        builder.Property(x => x.CycleStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.StopReason).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(x => x.LastReactivationReason).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.ActivityId, x.ReportingDate })
            .IsUnique()
            .HasDatabaseName("UX_ReportingCycle_Activity_Date");

        builder.HasOne(x => x.Activity)
            .WithMany()
            .HasForeignKey(x => x.ActivityId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.LastReactivatedByBranch)
            .WithMany()
            .HasForeignKey(x => x.LastReactivatedByBranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.LastReactivatedByPermission)
            .WithMany()
            .HasForeignKey(x => x.LastReactivatedByPermissionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class ReportingSlotConfiguration : IEntityTypeConfiguration<ReportingSlot>
{
    public void Configure(EntityTypeBuilder<ReportingSlot> builder)
    {
        builder.ToTable("ReportingSlot", DatabaseSchemas.Reporting);

        builder.HasKey(x => x.ReportingSlotId);
        builder.Property(x => x.SlotStatus).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(x => x.HangfireJobId).HasMaxLength(100);
        builder.Property(x => x.StatusNotes).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.ActivityId, x.ReportingDate, x.ScheduledSlotTime })
            .IsUnique()
            .HasDatabaseName("UX_ReportingSlot_Activity_Date_SlotTime");

        builder.HasOne(x => x.ReportingCycle)
            .WithMany(c => c.Slots)
            .HasForeignKey(x => x.ReportingCycleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Activity)
            .WithMany()
            .HasForeignKey(x => x.ActivityId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class SummaryLogConfiguration : IEntityTypeConfiguration<SummaryLog>
{
    public void Configure(EntityTypeBuilder<SummaryLog> builder)
    {
        builder.ToTable("SummaryLog", DatabaseSchemas.Reporting);

        builder.HasKey(x => x.ReportId);
        builder.Property(x => x.TriggerType).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.Property(x => x.CompletionPercentage).HasPrecision(5, 2);
        builder.Property(x => x.FormattedReportContent).IsRequired();
        builder.Property(x => x.TriggeredBy).HasMaxLength(150);

        builder.HasIndex(x => x.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("UX_SummaryLog_IdempotencyKey");

        builder.HasIndex(x => new { x.ActivityId, x.ReportingDate, x.GeneratedAtUtc })
            .HasDatabaseName("IX_SummaryLog_Activity_Date_GeneratedAt");

        builder.HasOne(x => x.ReportingCycle)
            .WithMany(c => c.Summaries)
            .HasForeignKey(x => x.ReportingCycleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Activity)
            .WithMany()
            .HasForeignKey(x => x.ActivityId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class ReportDeliveryLogConfiguration : IEntityTypeConfiguration<ReportDeliveryLog>
{
    public void Configure(EntityTypeBuilder<ReportDeliveryLog> builder)
    {
        builder.ToTable("ReportDeliveryLog", DatabaseSchemas.Reporting);

        builder.HasKey(x => x.ReportDeliveryId);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(250).IsRequired();
        builder.Property(x => x.DeliveryStatus).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(x => x.LastError).HasMaxLength(2000);
        builder.Property(x => x.DeliveredTelegramMessageIds).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);
        builder.Property(x => x.RowVersion).IsRowVersion();

        // Unique idempotency constraint per Activity + Date + Slot/FinalCycleKey + DestinationGroup
        builder.HasIndex(x => x.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("UX_ReportDeliveryLog_IdempotencyKey");

        // Composite unique constraint guaranteeing at most one delivery row per SummaryLog ReportId and DestinationGroupId
        builder.HasIndex(x => new { x.ReportId, x.DestinationGroupId })
            .IsUnique()
            .HasDatabaseName("UX_ReportDeliveryLog_ReportId_DestinationGroupId");

        builder.HasIndex(x => new { x.DeliveryStatus, x.ReportingDate })
            .HasDatabaseName("IX_ReportDeliveryLog_Status_Date");

        builder.HasOne(x => x.SummaryReport)
            .WithMany(s => s.Deliveries)
            .HasForeignKey(x => x.ReportId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.DestinationGroup)
            .WithMany()
            .HasForeignKey(x => x.DestinationGroupId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
