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
    [InlineData(typeof(OrganisationMappingHistory), "UX_OrganisationMappingHistory_ActivePerBranch")]
    [InlineData(typeof(ActivityMaster), "UX_ActivityMaster_ActivityCode")]
    [InlineData(typeof(ActivityStatus), "UX_ActivityStatus_Activity_Branch_Date")]
    [InlineData(typeof(StatusReopenPermission), "UX_StatusReopenPermission_ActivePerBranchDate")]
    [InlineData(typeof(TelegramMessageLog), "UX_TelegramMessageLog_ChatId_MessageId")]
    [InlineData(typeof(TelegramMessageLog), "UX_TelegramMessageLog_TelegramUpdateId")]
    [InlineData(typeof(ReportingCycle), "UX_ReportingCycle_Activity_Date")]
    [InlineData(typeof(ReportingSlot), "UX_ReportingSlot_Activity_Date_SlotTime")]
    [InlineData(typeof(SummaryLog), "UX_SummaryLog_IdempotencyKey")]
    [InlineData(typeof(ReportDeliveryLog), "UX_ReportDeliveryLog_IdempotencyKey")]
    [InlineData(typeof(ReportDeliveryLog), "UX_ReportDeliveryLog_ReportId_DestinationGroupId")]
    [InlineData(typeof(ActivityReportingConfiguration), "UX_ActivityReportingConfiguration_ActivePerActivity")]
    [InlineData(typeof(ActivityHistory), "UX_ActivityHistory_TelegramMessageLogId")]
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

    [Fact]
    public void EfModel_OrganisationMappingHistoryActiveIndex_ShouldHaveNullEffectiveToDateFilter()
    {
        using var context = CreateDesignTimeContext();
        var modelEntity = context.Model.FindEntityType(typeof(OrganisationMappingHistory));
        Assert.NotNull(modelEntity);

        var filteredIndex = modelEntity.GetIndexes()
            .Single(i => i.GetDatabaseName() == "UX_OrganisationMappingHistory_ActivePerBranch");

        Assert.True(filteredIndex.IsUnique);
        Assert.Equal("[EffectiveToDate] IS NULL", filteredIndex.GetFilter());
    }

    [Theory]
    [InlineData(typeof(BranchMaster))]
    [InlineData(typeof(OrganisationMappingHistory))]
    [InlineData(typeof(ActivityStatus))]
    [InlineData(typeof(StatusReopenPermission))]
    [InlineData(typeof(TelegramMessageLog))]
    [InlineData(typeof(ReportingCycle))]
    [InlineData(typeof(ReportingSlot))]
    [InlineData(typeof(ReportDeliveryLog))]
    [InlineData(typeof(ActivityReportingConfiguration))]
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
    public void EfModel_ActivityReportingConfigurationActiveIndex_ShouldHaveNullEffectiveToFilter()
    {
        using var context = CreateDesignTimeContext();
        var modelEntity = context.Model.FindEntityType(typeof(ActivityReportingConfiguration));
        Assert.NotNull(modelEntity);

        var filteredIndex = modelEntity.GetIndexes()
            .Single(i => i.GetDatabaseName() == "UX_ActivityReportingConfiguration_ActivePerActivity");

        Assert.True(filteredIndex.IsUnique);
        Assert.Equal("[EffectiveTo] IS NULL", filteredIndex.GetFilter());
    }

    [Fact]
    public void EfMigrations_ShouldContainExpectedMigrations()
    {
        using var context = CreateDesignTimeContext();
        var migrations = context.Database.GetMigrations().ToList();

        Assert.Contains(migrations, m => m.EndsWith("_InitialFoundation", StringComparison.Ordinal));
        Assert.Contains(migrations, m => m.EndsWith("_HardeningCoreInvariants", StringComparison.Ordinal));
        Assert.Contains(migrations, m => m.EndsWith("_OrganisationMasterInvariants", StringComparison.Ordinal));
        Assert.Contains(migrations, m => m.EndsWith("_ActivityConfigurationInvariants", StringComparison.Ordinal));
        Assert.Contains(migrations, m => m.EndsWith("_RffoSourceMessageIdempotency", StringComparison.Ordinal));
        Assert.Contains(migrations, m => m.EndsWith("_TelegramMessageIngestionAndIdempotency", StringComparison.Ordinal));
        Assert.Contains(migrations, m => m.EndsWith("_TelegramMessageProcessingConcurrencyAndState", StringComparison.Ordinal));
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

    [Fact]
    public async Task LiveDatabase_ShouldEnforceSingleActiveOrganisationMappingHistoryPerBranch()
    {
        await using var context = CreateDesignTimeContext();
        if (!await context.Database.CanConnectAsync())
        {
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();

            var zone = ZoneMaster.Create("TEST-ZONE", "Test Zone");
            context.Zones.Add(zone);
            await context.SaveChangesAsync();

            var state = StateMaster.Create(zone, "TEST-STATE", "Test State");
            context.States.Add(state);
            await context.SaveChangesAsync();

            var region = RegionMaster.Create(state, "TEST-REG", "Test Region");
            context.Regions.Add(region);
            await context.SaveChangesAsync();

            var branch = BranchMaster.CreateWithInitialHistory(
                zone,
                state,
                region,
                "99:99",
                "Test Transactional Branch",
                new DateOnly(2026, 11, 1));
            context.Branches.Add(branch);
            await context.SaveChangesAsync();

            // Attempt to insert a second open-ended (EffectiveToDate = null) history record for the same branch
            var duplicateOpenHistory = OrganisationMappingHistory.CreateOpenMapping(
                branch,
                zone,
                state,
                region,
                new DateOnly(2026, 11, 5),
                "Duplicate open history test",
                "test",
                DateTime.UtcNow);
            context.OrganisationMappingHistories.Add(duplicateOpenHistory);

            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

            await transaction.RollbackAsync();
        });
    }

    [Fact]
    public async Task LiveDatabase_ShouldEnforceSingleOpenActivityReportingConfigurationPerActivity()
    {
        await using var context = CreateDesignTimeContext();
        if (!await context.Database.CanConnectAsync())
        {
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();

            // ActivityId = 1 (RFFO) already has an open-ended (EffectiveTo = null) seeded configuration.
            // Attempting to insert a second open-ended configuration for ActivityId = 1 must violate UX_ActivityReportingConfiguration_ActivePerActivity.
            var duplicateOpenConfig = ActivityReportingConfiguration.Create(
                activityId: 1,
                reportingStartTime: new TimeOnly(10, 0),
                reportingEndTime: new TimeOnly(17, 0),
                reportingIntervalMinutes: 60,
                timeZoneId: "Asia/Kolkata",
                effectiveFrom: new DateOnly(2026, 12, 1));

            context.ActivityReportingConfigurations.Add(duplicateOpenConfig);
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

            await transaction.RollbackAsync();
        });
    }
}
