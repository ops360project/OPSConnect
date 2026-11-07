using Microsoft.EntityFrameworkCore;
using MiniExcelLibs;
using OpsConnect.Application.Common;
using OpsConnect.Application.Organisation;
using OpsConnect.Infrastructure.Persistence;
using OpsConnect.Infrastructure.Services;

namespace OpsConnect.IntegrationTests;

public class OrganisationMasterServiceTests
{
    private static readonly DateOnly Day1 = new(2026, 11, 1);
    private static readonly DateOnly Day5 = new(2026, 11, 5);
    private static readonly DateOnly Day10 = new(2026, 11, 10);

    private static OpsConnectDbContext CreateContext()
    {
        var factory = new OpsConnectDbContextFactory();
        return factory.CreateDbContext(Array.Empty<string>());
    }

    private static OrganisationMasterService CreateService(OpsConnectDbContext context)
    {
        var reader = new OrganisationExcelReader();
        var validator = new OrganisationHierarchyImportValidator();
        var clock = new SystemBusinessClock();
        return new OrganisationMasterService(context, reader, validator, clock);
    }

    private static OrganisationImportRow[] CreateSampleBatch() =>
    [
        new OrganisationImportRow(
            RowNumber: 2,
            ZoneCode: "EAST",
            ZoneName: "East Zone",
            StateCode: "OD",
            StateName: "Odisha",
            RegionCode: "REG-A",
            RegionName: "Region A",
            BranchCode: "36:01",
            BranchName: "Bhubaneswar Main",
            IsZoneActive: true,
            IsStateActive: true,
            IsRegionActive: true,
            IsBranchActive: true),
        new OrganisationImportRow(
            RowNumber: 3,
            ZoneCode: "EAST",
            ZoneName: "East Zone",
            StateCode: "OD",
            StateName: "Odisha",
            RegionCode: "REG-A",
            RegionName: "Region A",
            BranchCode: "36:02",
            BranchName: "Cuttack City",
            IsZoneActive: true,
            IsStateActive: true,
            IsRegionActive: true,
            IsBranchActive: true),
        new OrganisationImportRow(
            RowNumber: 4,
            ZoneCode: "EAST",
            ZoneName: "East Zone",
            StateCode: "OD",
            StateName: "Odisha",
            RegionCode: "REG-B",
            RegionName: "Region B",
            BranchCode: "36:03",
            BranchName: "Rourkela Inactive Branch",
            IsZoneActive: true,
            IsStateActive: true,
            IsRegionActive: true,
            IsBranchActive: false)
    ];

    [Fact]
    public async Task ImportHierarchyAsync_AndQueries_ShouldPersistAndFilterCorrectly()
    {
        await using var context = CreateContext();
        if (!await context.Database.CanConnectAsync())
        {
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync();
            var service = CreateService(context);

            var result = await service.ImportHierarchyAsync(
                rows: CreateSampleBatch(),
                effectiveFromDate: Day1,
                validateOnly: false,
                importedBy: "admin.test",
                changeReason: "Initial rollout");

            Assert.True(result.ValidationReport.IsValid);
            Assert.True(result.WasPersisted);
            Assert.Equal(1, result.ZonesCreated);
            Assert.Equal(1, result.StatesCreated);
            Assert.Equal(2, result.RegionsCreated);
            Assert.Equal(3, result.BranchesCreated);
            Assert.Equal(3, result.MappingHistoriesCreated);

            // Filter active branches only
            var activeBranches = await service.GetBranchesAsync(new BranchFilterQuery(IsActive: true));
            Assert.Equal(2, activeBranches.Count);
            Assert.All(activeBranches, b => Assert.True(b.CanSubmitActivityUpdates));

            // Filter by RegionCode and SearchTerm
            var regABranches = await service.GetBranchesAsync(
                new BranchFilterQuery(RegionCode: "reg-a", SearchTerm: "Cuttack"));
            var cuttack = Assert.Single(regABranches);
            Assert.Equal("36:02", cuttack.BranchCode);

            // Hierarchy tree (active only vs includeInactive)
            var activeTree = await service.GetHierarchyTreeAsync(includeInactive: false);
            var eastZone = Assert.Single(activeTree);
            var odState = Assert.Single(eastZone.States);
            Assert.Equal(2, odState.Regions.SelectMany(r => r.Branches).Count());

            var fullTree = await service.GetHierarchyTreeAsync(includeInactive: true);
            Assert.Equal(3, fullTree.Single().States.Single().Regions.SelectMany(r => r.Branches).Count());

            await tx.RollbackAsync();
        });
    }

    [Fact]
    public async Task ImportHierarchyFromExcelAsync_WithValidateOnly_ShouldNotPersistAnyRecords()
    {
        await using var context = CreateContext();
        if (!await context.Database.CanConnectAsync())
        {
            return;
        }

        var excelData = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["ZoneCode"] = "EAST",
                ["ZoneName"] = "East Zone",
                ["StateCode"] = "OD",
                ["StateName"] = "Odisha",
                ["RegionCode"] = "REG-A",
                ["RegionName"] = "Region A",
                ["BranchCode"] = "36:01",
                ["BranchName"] = "Bhubaneswar Main",
                ["IsActive"] = "Active"
            }
        };

        await using var stream = new MemoryStream();
        await stream.SaveAsAsync(excelData);
        stream.Position = 0;

        var service = CreateService(context);
        var result = await service.ImportHierarchyFromExcelAsync(
            excelStream: stream,
            effectiveFromDate: Day1,
            validateOnly: true,
            importedBy: "admin.test");

        Assert.True(result.ValidateOnly);
        Assert.False(result.WasPersisted);
        Assert.True(result.ValidationReport.IsValid);
        Assert.Equal(1, result.ValidationReport.ValidRowCount);

        Assert.Equal(0, await context.Branches.CountAsync());
        Assert.Equal(0, await context.Zones.CountAsync());
    }

    [Fact]
    public async Task ImportHierarchyAsync_WithDuplicateOrInvalidRow_ShouldPersistZeroRecords()
    {
        await using var context = CreateContext();
        if (!await context.Database.CanConnectAsync())
        {
            return;
        }

        var invalidBatch = new[]
        {
            CreateSampleBatch()[0],
            // Duplicate BranchCode 36:01 on row 3
            CreateSampleBatch()[0] with { RowNumber = 3, BranchName = "Duplicate 36:01" }
        };

        var service = CreateService(context);
        var result = await service.ImportHierarchyAsync(invalidBatch, effectiveFromDate: Day1);

        Assert.False(result.ValidationReport.IsValid);
        Assert.False(result.WasPersisted);
        Assert.Contains(
            result.ValidationReport.Errors,
            e => e.ErrorCode == OrganisationImportErrorCode.DuplicateBranchCode);

        Assert.Equal(0, await context.Branches.CountAsync());
        Assert.Equal(0, await context.Zones.CountAsync());
    }

    [Fact]
    public async Task ImportHierarchyAsync_UnchangedReImport_ShouldNotCreateDuplicateMappingHistory()
    {
        await using var context = CreateContext();
        if (!await context.Database.CanConnectAsync())
        {
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync();
            var service = CreateService(context);

            var batch = CreateSampleBatch();
            var firstImport = await service.ImportHierarchyAsync(batch, effectiveFromDate: Day1);
            Assert.True(firstImport.WasPersisted);
            Assert.Equal(3, firstImport.MappingHistoriesCreated);

            // Re-import the exact same batch on Day 5
            var secondImport = await service.ImportHierarchyAsync(batch, effectiveFromDate: Day5);
            Assert.True(secondImport.WasPersisted);
            Assert.Equal(0, secondImport.ZonesCreated);
            Assert.Equal(0, secondImport.ZonesUpdated);
            Assert.Equal(0, secondImport.BranchesCreated);
            Assert.Equal(0, secondImport.BranchesUpdated);
            Assert.Equal(0, secondImport.MappingHistoriesCreated);

            var history = await service.GetBranchMappingHistoryAsync("36:01", asOfReportingDate: Day5);
            Assert.NotNull(history);
            Assert.Single(history.HistoryEntries);

            await tx.RollbackAsync();
        });
    }

    [Fact]
    public async Task ImportHierarchyAsync_WhenBranchMovesRegion_ShouldClosePreviousHistoryAndResolveHistoricalDates()
    {
        await using var context = CreateContext();
        if (!await context.Database.CanConnectAsync())
        {
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync();
            var service = CreateService(context);

            // Day 1: Branch 36:01 in REG-A
            await service.ImportHierarchyAsync(CreateSampleBatch(), effectiveFromDate: Day1);

            // Day 10: Move Branch 36:01 from REG-A to REG-B
            var movedBatch = new[]
            {
                new OrganisationImportRow(
                    RowNumber: 2,
                    ZoneCode: "EAST",
                    ZoneName: "East Zone",
                    StateCode: "OD",
                    StateName: "Odisha",
                    RegionCode: "REG-B",
                    RegionName: "Region B",
                    BranchCode: "36:01",
                    BranchName: "Bhubaneswar Main")
            };

            var updateResult = await service.ImportHierarchyAsync(
                movedBatch,
                effectiveFromDate: Day10,
                importedBy: "admin.ops",
                changeReason: "Moved 36:01 to Region B");

            Assert.True(updateResult.WasPersisted);
            Assert.Equal(0, updateResult.BranchesCreated);
            Assert.Equal(1, updateResult.BranchesUpdated);
            Assert.Equal(1, updateResult.MappingHistoriesCreated);

            // Query historical date Day 5 (2026-11-05) -> must resolve to REG-A
            var historyAsOfDay5 = await service.GetBranchMappingHistoryAsync("36:01", asOfReportingDate: Day5);
            Assert.NotNull(historyAsOfDay5);
            Assert.Equal(2, historyAsOfDay5.HistoryEntries.Count);
            Assert.NotNull(historyAsOfDay5.ResolvedMappingAsOfDate);
            Assert.Equal("REG-A", historyAsOfDay5.ResolvedMappingAsOfDate.RegionCode);
            Assert.Equal(new DateOnly(2026, 11, 9), historyAsOfDay5.ResolvedMappingAsOfDate.EffectiveToDate);
            Assert.False(historyAsOfDay5.ResolvedMappingAsOfDate.IsCurrentActiveMapping);

            // Query date Day 10 (2026-11-10) -> must resolve to REG-B
            var historyAsOfDay10 = await service.GetBranchMappingHistoryAsync("36:01", asOfReportingDate: Day10);
            Assert.NotNull(historyAsOfDay10?.ResolvedMappingAsOfDate);
            Assert.Equal("REG-B", historyAsOfDay10.ResolvedMappingAsOfDate.RegionCode);
            Assert.Null(historyAsOfDay10.ResolvedMappingAsOfDate.EffectiveToDate);
            Assert.True(historyAsOfDay10.ResolvedMappingAsOfDate.IsCurrentActiveMapping);

            // Attempt retroactive hierarchy change with EffectiveFromDate earlier than Day 10 (e.g., Day 5) -> rejected before persistence
            var retroactiveBatch = new[]
            {
                movedBatch[0] with { RegionCode = "REG-A", RegionName = "Region A" }
            };

            var retroactiveResult = await service.ImportHierarchyAsync(
                retroactiveBatch,
                effectiveFromDate: Day5);

            Assert.False(retroactiveResult.ValidationReport.IsValid);
            Assert.False(retroactiveResult.WasPersisted);
            Assert.Contains(
                retroactiveResult.ValidationReport.Errors,
                e => e.ErrorCode == OrganisationImportErrorCode.InvalidEffectiveDateTransition);

            await tx.RollbackAsync();
        });
    }

    [Fact]
    public async Task ConcurrencySafeguard_StaleRowVersionOnBranch_ShouldThrowDbUpdateConcurrencyException()
    {
        await using var context = CreateContext();
        if (!await context.Database.CanConnectAsync())
        {
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync();
            var service = CreateService(context);

            await service.ImportHierarchyAsync(CreateSampleBatch(), effectiveFromDate: Day1);

            var branch = await context.Branches.SingleAsync(b => b.BranchCode == "36:01");
            var staleRowVersion = branch.RowVersion.ToArray();

            // First update increments RowVersion in SQL Server
            branch.BranchName = "Bhubaneswar Main Updated 1";
            await context.SaveChangesAsync();

            // Simulate a concurrent caller attempting to save with the stale RowVersion token
            branch.BranchName = "Bhubaneswar Main Concurrent Overwrite";
            context.Entry(branch).Property(b => b.RowVersion).OriginalValue = staleRowVersion;

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync());

            await tx.RollbackAsync();
        });
    }
}
