using Microsoft.EntityFrameworkCore;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Common;
using OpsConnect.Domain.Organisation;
using OpsConnect.Domain.Reporting;
using OpsConnect.Domain.Telegram;
using OpsConnect.Infrastructure.Persistence;

namespace OpsConnect.IntegrationTests;

public class DatabaseFoundationTests
{
    private static OpsConnectDbContext CreateDesignTimeContext()
    {
        var factory = new OpsConnectDbContextFactory();
        return factory.CreateDbContext(Array.Empty<string>());
    }

    [Theory]
    [InlineData(typeof(ZoneMaster), DatabaseSchemas.Organisation, "ZoneMaster")]
    [InlineData(typeof(StateMaster), DatabaseSchemas.Organisation, "StateMaster")]
    [InlineData(typeof(RegionMaster), DatabaseSchemas.Organisation, "RegionMaster")]
    [InlineData(typeof(BranchMaster), DatabaseSchemas.Organisation, "BranchMaster")]
    [InlineData(typeof(OrganisationMappingHistory), DatabaseSchemas.Organisation, "OrganisationMappingHistory")]
    [InlineData(typeof(ActivityMaster), DatabaseSchemas.Activity, "ActivityMaster")]
    [InlineData(typeof(ActivityStatus), DatabaseSchemas.Activity, "ActivityStatus")]
    [InlineData(typeof(ActivityHistory), DatabaseSchemas.Activity, "ActivityHistory")]
    [InlineData(typeof(StatusReopenPermission), DatabaseSchemas.Activity, "StatusReopenPermission")]
    [InlineData(typeof(TelegramGroup), DatabaseSchemas.Telegram, "TelegramGroups")]
    [InlineData(typeof(TelegramMessageLog), DatabaseSchemas.Telegram, "TelegramMessageLog")]
    [InlineData(typeof(ReportingCycle), DatabaseSchemas.Reporting, "ReportingCycle")]
    [InlineData(typeof(ReportingSlot), DatabaseSchemas.Reporting, "ReportingSlot")]
    [InlineData(typeof(SummaryLog), DatabaseSchemas.Reporting, "SummaryLog")]
    [InlineData(typeof(ReportDeliveryLog), DatabaseSchemas.Reporting, "ReportDeliveryLog")]
    [InlineData(typeof(UserMaster), DatabaseSchemas.Administration, "UserMaster")]
    [InlineData(typeof(RoleMaster), DatabaseSchemas.Administration, "RoleMaster")]
    [InlineData(typeof(UserRoleMapping), DatabaseSchemas.Administration, "UserRoleMapping")]
    [InlineData(typeof(SystemConfiguration), DatabaseSchemas.Administration, "SystemConfiguration")]
    [InlineData(typeof(ActivityConfiguration), DatabaseSchemas.Administration, "ActivityConfiguration")]
    [InlineData(typeof(ActivityReportingConfiguration), DatabaseSchemas.Administration, "ActivityReportingConfiguration")]
    [InlineData(typeof(ConfigurationHistory), DatabaseSchemas.Administration, "ConfigurationHistory")]
    public void EfModel_ShouldMapAllEntitiesToExpectedSchemasAndTables(
        Type entityType,
        string expectedSchema,
        string expectedTable)
    {
        using var context = CreateDesignTimeContext();
        var modelEntity = context.Model.FindEntityType(entityType);

        Assert.NotNull(modelEntity);
        Assert.Equal(expectedSchema, modelEntity.GetSchema());
        Assert.Equal(expectedTable, modelEntity.GetTableName());
    }

    [Theory]
    [InlineData(typeof(BranchMaster), "UX_BranchMaster_BranchCode")]
    [InlineData(typeof(ActivityMaster), "UX_ActivityMaster_ActivityCode")]
    [InlineData(typeof(ActivityStatus), "UX_ActivityStatus_Activity_Branch_Date")]
    [InlineData(typeof(StatusReopenPermission), "UX_StatusReopenPermission_ActivePerBranchDate")]
    [InlineData(typeof(TelegramMessageLog), "UX_TelegramMessageLog_ChatId_MessageId")]
    [InlineData(typeof(ReportingCycle), "UX_ReportingCycle_Activity_Date")]
    [InlineData(typeof(ReportingSlot), "UX_ReportingSlot_Activity_Date_SlotTime")]
    [InlineData(typeof(SummaryLog), "UX_SummaryLog_IdempotencyKey")]
    [InlineData(typeof(ReportDeliveryLog), "UX_ReportDeliveryLog_IdempotencyKey")]
    [InlineData(typeof(ReportDeliveryLog), "UX_ReportDeliveryLog_ReportId_DestinationGroupId")]
    public void EfModel_ShouldDefineCriticalUniqueConstraints(
        Type entityType,
        string expectedIndexName)
    {
        using var context = CreateDesignTimeContext();
        var modelEntity = context.Model.FindEntityType(entityType);

        Assert.NotNull(modelEntity);
        var matchingIndex = modelEntity.GetIndexes()
            .FirstOrDefault(i => i.GetDatabaseName() == expectedIndexName);

        Assert.NotNull(matchingIndex);
        Assert.True(matchingIndex.IsUnique);
    }

    [Fact]
    public void EfModel_StatusReopenPermissionActiveIndex_ShouldHaveAvailableFilter()
    {
        using var context = CreateDesignTimeContext();
        var modelEntity = context.Model.FindEntityType(typeof(StatusReopenPermission));
        Assert.NotNull(modelEntity);

        var filteredIndex = modelEntity.GetIndexes()
            .Single(i => i.GetDatabaseName() == "UX_StatusReopenPermission_ActivePerBranchDate");

        Assert.True(filteredIndex.IsUnique);
        Assert.Equal("[PermissionStatus] = 'Available'", filteredIndex.GetFilter());
    }

    [Theory]
    [InlineData(typeof(ActivityStatus))]
    [InlineData(typeof(StatusReopenPermission))]
    [InlineData(typeof(ReportingCycle))]
    [InlineData(typeof(ReportingSlot))]
    [InlineData(typeof(ReportDeliveryLog))]
    public void EfModel_ShouldConfigureRowVersionConcurrencyToken(Type entityType)
    {
        using var context = CreateDesignTimeContext();
        var modelEntity = context.Model.FindEntityType(entityType);

        Assert.NotNull(modelEntity);
        var rowVersionProperty = modelEntity.FindProperty("RowVersion");
        Assert.NotNull(rowVersionProperty);
        Assert.True(rowVersionProperty.IsConcurrencyToken);
    }

    [Fact]
    public void EfMigrations_ShouldContainInitialFoundationMigration()
    {
        using var context = CreateDesignTimeContext();
        var migrations = context.Database.GetMigrations().ToList();

        Assert.Contains(migrations, m => m.EndsWith("_InitialFoundation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LiveDatabase_ShouldHaveAppliedMigrationsAndSeededFoundationRecords()
    {
        await using var context = CreateDesignTimeContext();
        if (!await context.Database.CanConnectAsync())
        {
            return;
        }

        var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
        Assert.Empty(pendingMigrations);

        var rffo = await context.Activities.AsNoTracking().SingleOrDefaultAsync(a => a.ActivityCode == "RFFO");
        Assert.NotNull(rffo);
        Assert.True(rffo.IsActive);

        var rffoSchedule = await context.ActivityReportingConfigurations
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.ActivityId == rffo.ActivityId);
        Assert.NotNull(rffoSchedule);
        Assert.Equal(new TimeOnly(9, 0), rffoSchedule.ReportingStartTime);
        Assert.Equal(new TimeOnly(18, 0), rffoSchedule.ReportingEndTime);
        Assert.Equal(30, rffoSchedule.ReportingIntervalMinutes);
        Assert.Equal("Asia/Kolkata", rffoSchedule.TimeZoneId);
        Assert.True(rffoSchedule.IsReportingEnabled);
        Assert.True(rffoSchedule.StopWhenAllCompleted);

        var roles = await context.Roles.AsNoTracking().Select(r => r.RoleName).ToListAsync();
        Assert.Contains(SystemRoles.SuperAdmin, roles);
        Assert.Contains(SystemRoles.Admin, roles);
        Assert.Contains(SystemRoles.Viewer, roles);

        var branchCount = await context.Branches.CountAsync();
        Assert.Equal(0, branchCount);
    }
}
