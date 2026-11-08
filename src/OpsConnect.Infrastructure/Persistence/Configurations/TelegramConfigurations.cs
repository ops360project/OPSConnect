using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsConnect.Domain.Common;
using OpsConnect.Domain.Telegram;

namespace OpsConnect.Infrastructure.Persistence.Configurations;

public class TelegramGroupConfiguration : IEntityTypeConfiguration<TelegramGroup>
{
    public void Configure(EntityTypeBuilder<TelegramGroup> builder)
    {
        builder.ToTable("TelegramGroups", DatabaseSchemas.Telegram);

        builder.HasKey(x => x.TelegramGroupId);
        builder.Property(x => x.GroupTitle).HasMaxLength(250).IsRequired();
        builder.Property(x => x.GroupRole).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);

        builder.HasIndex(x => new { x.TelegramChatId, x.ActivityId, x.GroupRole })
            .IsUnique()
            .HasDatabaseName("UX_TelegramGroups_ChatId_Activity_Role");

        builder.HasOne(x => x.Activity)
            .WithMany()
            .HasForeignKey(x => x.ActivityId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class TelegramMessageLogConfiguration : IEntityTypeConfiguration<TelegramMessageLog>
{
    public void Configure(EntityTypeBuilder<TelegramMessageLog> builder)
    {
        builder.ToTable("TelegramMessageLog", DatabaseSchemas.Telegram);

        builder.HasKey(x => x.TelegramMessageLogId);
        builder.Property(x => x.SourceGroupTitle).HasMaxLength(250);
        builder.Property(x => x.SenderUsername).HasMaxLength(150);
        builder.Property(x => x.SenderDisplayName).HasMaxLength(200);
        builder.Property(x => x.RawMessageText).HasMaxLength(4000).IsRequired();
        builder.Property(x => x.ParsedActivityCode).HasMaxLength(50);
        builder.Property(x => x.ParsedBranchCode).HasMaxLength(50);
        builder.Property(x => x.ParsedStatusText).HasMaxLength(50);
        builder.Property(x => x.ProcessingStatus).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(x => x.RejectionReason).HasMaxLength(1000);
        builder.Property(x => x.RowVersion).IsRowVersion().IsConcurrencyToken();

        // Unique duplicate-detection key 1: Telegram Update ID (when present)
        builder.HasIndex(x => x.TelegramUpdateId)
            .IsUnique()
            .HasFilter("[TelegramUpdateId] IS NOT NULL")
            .HasDatabaseName("UX_TelegramMessageLog_TelegramUpdateId");

        // Unique duplicate-detection key 2: Telegram Chat ID + Telegram Message ID
        builder.HasIndex(x => new { x.TelegramChatId, x.TelegramMessageId })
            .IsUnique()
            .HasDatabaseName("UX_TelegramMessageLog_ChatId_MessageId");

        builder.HasIndex(x => new { x.ReportingDate, x.ActivityId, x.BranchId })
            .HasDatabaseName("IX_TelegramMessageLog_Date_Activity_Branch");
    }
}
