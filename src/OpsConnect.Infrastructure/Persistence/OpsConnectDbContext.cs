using Microsoft.EntityFrameworkCore;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Common;
using OpsConnect.Domain.Organisation;
using OpsConnect.Domain.Reporting;
using OpsConnect.Domain.Telegram;

namespace OpsConnect.Infrastructure.Persistence;

public class OpsConnectDbContext : DbContext
{
    public OpsConnectDbContext(DbContextOptions<OpsConnectDbContext> options)
        : base(options)
    {
    }

    // org schema
    public DbSet<ZoneMaster> Zones => Set<ZoneMaster>();
    public DbSet<StateMaster> States => Set<StateMaster>();
    public DbSet<RegionMaster> Regions => Set<RegionMaster>();
    public DbSet<BranchMaster> Branches => Set<BranchMaster>();
    public DbSet<OrganisationMappingHistory> OrganisationMappingHistories => Set<OrganisationMappingHistory>();

    // activity schema
    public DbSet<ActivityMaster> Activities => Set<ActivityMaster>();
    public DbSet<ActivityStatus> ActivityStatuses => Set<ActivityStatus>();
    public DbSet<ActivityHistory> ActivityHistories => Set<ActivityHistory>();
    public DbSet<StatusReopenPermission> StatusReopenPermissions => Set<StatusReopenPermission>();

    // telegram schema
    public DbSet<TelegramGroup> TelegramGroups => Set<TelegramGroup>();
    public DbSet<TelegramMessageLog> TelegramMessageLogs => Set<TelegramMessageLog>();

    // reporting schema
    public DbSet<ReportingCycle> ReportingCycles => Set<ReportingCycle>();
    public DbSet<ReportingSlot> ReportingSlots => Set<ReportingSlot>();
    public DbSet<SummaryLog> SummaryLogs => Set<SummaryLog>();
    public DbSet<ReportDeliveryLog> ReportDeliveryLogs => Set<ReportDeliveryLog>();

    // admin schema
    public DbSet<UserMaster> Users => Set<UserMaster>();
    public DbSet<RoleMaster> Roles => Set<RoleMaster>();
    public DbSet<UserRoleMapping> UserRoleMappings => Set<UserRoleMapping>();
    public DbSet<SystemConfiguration> SystemConfigurations => Set<SystemConfiguration>();
    public DbSet<ActivityConfiguration> ActivityConfigurations => Set<ActivityConfiguration>();
    public DbSet<ActivityReportingConfiguration> ActivityReportingConfigurations => Set<ActivityReportingConfiguration>();
    public DbSet<ConfigurationHistory> ConfigurationHistories => Set<ConfigurationHistory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OpsConnectDbContext).Assembly);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyAuditTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        ApplyAuditTimestamps();
        return base.SaveChanges();
    }

    private void ApplyAuditTimestamps()
    {
        var utcNow = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CreatedAtUtc == default)
                {
                    entry.Entity.CreatedAtUtc = utcNow;
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAtUtc = utcNow;
            }
        }
    }
}
