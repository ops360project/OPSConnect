using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsConnect.Domain.Common;
using OpsConnect.Domain.Organisation;

namespace OpsConnect.Infrastructure.Persistence.Configurations;

public class ZoneMasterConfiguration : IEntityTypeConfiguration<ZoneMaster>
{
    public void Configure(EntityTypeBuilder<ZoneMaster> builder)
    {
        builder.ToTable("ZoneMaster", DatabaseSchemas.Organisation);

        builder.HasKey(x => x.ZoneId);
        builder.Property(x => x.ZoneCode).HasMaxLength(50).IsRequired();
        builder.Property(x => x.ZoneName).HasMaxLength(150).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);

        builder.HasIndex(x => x.ZoneCode)
            .IsUnique()
            .HasDatabaseName("UX_ZoneMaster_ZoneCode");
    }
}

public class StateMasterConfiguration : IEntityTypeConfiguration<StateMaster>
{
    public void Configure(EntityTypeBuilder<StateMaster> builder)
    {
        builder.ToTable("StateMaster", DatabaseSchemas.Organisation);

        builder.HasKey(x => x.StateId);
        builder.Property(x => x.StateCode).HasMaxLength(50).IsRequired();
        builder.Property(x => x.StateName).HasMaxLength(150).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);

        builder.HasIndex(x => x.StateCode)
            .IsUnique()
            .HasDatabaseName("UX_StateMaster_StateCode");

        builder.HasIndex(x => x.ZoneId)
            .HasDatabaseName("IX_StateMaster_ZoneId");

        builder.HasOne(x => x.Zone)
            .WithMany(z => z.States)
            .HasForeignKey(x => x.ZoneId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class RegionMasterConfiguration : IEntityTypeConfiguration<RegionMaster>
{
    public void Configure(EntityTypeBuilder<RegionMaster> builder)
    {
        builder.ToTable("RegionMaster", DatabaseSchemas.Organisation);

        builder.HasKey(x => x.RegionId);
        builder.Property(x => x.RegionCode).HasMaxLength(50).IsRequired();
        builder.Property(x => x.RegionName).HasMaxLength(150).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);

        builder.HasIndex(x => x.RegionCode)
            .IsUnique()
            .HasDatabaseName("UX_RegionMaster_RegionCode");

        builder.HasIndex(x => x.StateId)
            .HasDatabaseName("IX_RegionMaster_StateId");

        builder.HasOne(x => x.State)
            .WithMany(s => s.Regions)
            .HasForeignKey(x => x.StateId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class BranchMasterConfiguration : IEntityTypeConfiguration<BranchMaster>
{
    public void Configure(EntityTypeBuilder<BranchMaster> builder)
    {
        builder.ToTable("BranchMaster", DatabaseSchemas.Organisation);

        builder.HasKey(x => x.BranchId);
        builder.Property(x => x.BranchCode).HasMaxLength(50).IsRequired();
        builder.Property(x => x.BranchName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);

        builder.HasIndex(x => x.BranchCode)
            .IsUnique()
            .HasDatabaseName("UX_BranchMaster_BranchCode");

        builder.HasIndex(x => new { x.IsActive, x.StateId, x.RegionId })
            .HasDatabaseName("IX_BranchMaster_Active_State_Region");

        builder.HasOne(x => x.Zone)
            .WithMany(z => z.Branches)
            .HasForeignKey(x => x.ZoneId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.State)
            .WithMany(s => s.Branches)
            .HasForeignKey(x => x.StateId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Region)
            .WithMany(r => r.Branches)
            .HasForeignKey(x => x.RegionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class OrganisationMappingHistoryConfiguration : IEntityTypeConfiguration<OrganisationMappingHistory>
{
    public void Configure(EntityTypeBuilder<OrganisationMappingHistory> builder)
    {
        builder.ToTable("OrganisationMappingHistory", DatabaseSchemas.Organisation);

        builder.HasKey(x => x.MappingHistoryId);
        builder.Property(x => x.BranchCode).HasMaxLength(50).IsRequired();
        builder.Property(x => x.ZoneCode).HasMaxLength(50).IsRequired();
        builder.Property(x => x.ZoneName).HasMaxLength(150).IsRequired();
        builder.Property(x => x.StateCode).HasMaxLength(50).IsRequired();
        builder.Property(x => x.StateName).HasMaxLength(150).IsRequired();
        builder.Property(x => x.RegionCode).HasMaxLength(50).IsRequired();
        builder.Property(x => x.RegionName).HasMaxLength(150).IsRequired();
        builder.Property(x => x.ChangeReason).HasMaxLength(500);
        builder.Property(x => x.RecordedBy).HasMaxLength(100);

        builder.HasIndex(x => new { x.BranchId, x.EffectiveFromDate })
            .HasDatabaseName("IX_OrganisationMappingHistory_BranchId_EffectiveFromDate");

        builder.HasOne(x => x.Branch)
            .WithMany(b => b.MappingHistories)
            .HasForeignKey(x => x.BranchId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
