using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpsConnect.Api.Authentication;
using OpsConnect.Api.Controllers;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Common;
using OpsConnect.Application.Organisation;
using OpsConnect.Application.Reporting;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Organisation;
using OpsConnect.Domain.Reporting;
using OpsConnect.Infrastructure.Persistence;
using OpsConnect.Infrastructure.Services;
using Xunit;

namespace OpsConnect.IntegrationTests;

public class ReportingAggregationIntegrationTests
{
    private static readonly DateOnly Day1 = new(2026, 11, 10);
    private static readonly DateOnly Day2 = new(2026, 11, 11);

    private static OpsConnectDbContext CreateContext()
    {
        var factory = new OpsConnectDbContextFactory();
        return factory.CreateDbContext(Array.Empty<string>());
    }

    private static ClaimsPrincipal CreatePrincipal(string? username, string? role, bool isAuthenticated = true)
    {
        if (!isAuthenticated || string.IsNullOrWhiteSpace(username))
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username)
        };

        if (!string.IsNullOrWhiteSpace(role))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, OpsConnectAuthenticationHandler.SchemeName));
    }

    private static (
        IOrganisationMasterService OrgService,
        IActivityReportingService ReportingService,
        ReportingController Controller)
        CreateServices(
            OpsConnectDbContext dbContext,
            string? username = "admin.operator",
            string? role = SystemRoles.Admin,
            bool isAuthenticated = true)
    {
        var clock = new SystemBusinessClock();

        var orgService = new OrganisationMasterService(
            dbContext,
            new OrganisationExcelReader(),
            new OrganisationHierarchyImportValidator(),
            clock);

        var reportingService = new ActivityReportingService(dbContext, clock);

        var principal = CreatePrincipal(username, role, isAuthenticated);

        var controller = new ReportingController(reportingService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };

        return (orgService, reportingService, controller);
    }

    private static OrganisationImportRow[] CreateTestHierarchyBatch() =>
    [
        new(
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
        new(
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
        new(
            RowNumber: 4,
            ZoneCode: "EAST",
            ZoneName: "East Zone",
            StateCode: "OD",
            StateName: "Odisha",
            RegionCode: "REG-B",
            RegionName: "Region B",
            BranchCode: "36:03",
            BranchName: "Berhampur Town",
            IsZoneActive: true,
            IsStateActive: true,
            IsRegionActive: true,
            IsBranchActive: true),
        new(
            RowNumber: 5,
            ZoneCode: "WEST",
            ZoneName: "West Zone",
            StateCode: "MH",
            StateName: "Maharashtra",
            RegionCode: "REG-W",
            RegionName: "Region West",
            BranchCode: "36:04",
            BranchName: "Mumbai Central",
            IsZoneActive: true,
            IsStateActive: true,
            IsRegionActive: true,
            IsBranchActive: true),
        new(
            RowNumber: 6,
            ZoneCode: "WEST",
            ZoneName: "West Zone",
            StateCode: "MH",
            StateName: "Maharashtra",
            RegionCode: "REG-W",
            RegionName: "Region West",
            BranchCode: "36:05",
            BranchName: "Pune Suburban",
            IsZoneActive: true,
            IsStateActive: true,
            IsRegionActive: true,
            IsBranchActive: true),
        new(
            RowNumber: 7,
            ZoneCode: "WEST",
            ZoneName: "West Zone",
            StateCode: "MH",
            StateName: "Maharashtra",
            RegionCode: "REG-W",
            RegionName: "Region West",
            BranchCode: "36:99",
            BranchName: "Inactive Branch",
            IsZoneActive: true,
            IsStateActive: true,
            IsRegionActive: true,
            IsBranchActive: false)
    ];

    [Fact]
    public async Task Reporting_AllBranchesNotReported_ShouldReturnZeroCompletedAndFullNotReported()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, reportingService, controller) = CreateServices(dbContext);
        await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: Day1);

        var query = new ReportingFilterQuery
        {
            ActivityCode = ActivityModuleConstants.RffoActivityCode,
            ReportingDate = Day1
        };

        var report = await reportingService.GetActivitySummaryReportAsync(query);

        // 5 active branches (36:99 is inactive)
        Assert.Equal(5, report.TotalBranches);
        Assert.Equal(0, report.CompletedBranches);
        Assert.Equal(0, report.SubmittedPendingBranches);
        Assert.Equal(5, report.NotReportedBranches);
        Assert.Equal(5, report.PendingBranches); // Pending count = Pending + NotReported
        Assert.Equal(0.00m, report.CompletionPercentage);
        Assert.False(report.IsEmptyPopulation);
        Assert.False(report.IsFullyCompleted);

        // Verify branch details
        var branchDetails = await reportingService.GetBranchReportDetailsAsync(query);
        Assert.Equal(5, branchDetails.TotalMatchingBranches);
        Assert.All(branchDetails.Branches, b =>
        {
            Assert.Equal(BranchActivityStatus.NotReported, b.CurrentStatus);
            Assert.True(b.IsDerivedNotReported);
        });

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Reporting_AllBranchesPending_ShouldReturnZeroCompletedAndAllPending()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, reportingService, _) = CreateServices(dbContext);
        await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: Day1);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == ActivityModuleConstants.RffoActivityCode);
        var activeBranches = await dbContext.Branches.Where(b => b.IsActive).ToListAsync();

        foreach (var branch in activeBranches)
        {
            dbContext.ActivityStatuses.Add(new ActivityStatus
            {
                ActivityId = activity.ActivityId,
                BranchId = branch.BranchId,
                ReportingDate = Day1,
                CurrentStatus = BranchActivityStatus.Pending,
                LastUpdatedByDisplayName = "Test Submitter"
            });
        }
        await dbContext.SaveChangesAsync();

        var report = await reportingService.GetActivitySummaryReportAsync(new ReportingFilterQuery
        {
            ActivityCode = ActivityModuleConstants.RffoActivityCode,
            ReportingDate = Day1
        });

        Assert.Equal(5, report.TotalBranches);
        Assert.Equal(0, report.CompletedBranches);
        Assert.Equal(5, report.SubmittedPendingBranches);
        Assert.Equal(0, report.NotReportedBranches);
        Assert.Equal(5, report.PendingBranches);
        Assert.Equal(0.00m, report.CompletionPercentage);
        Assert.False(report.IsEmptyPopulation);
        Assert.False(report.IsFullyCompleted);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Reporting_AllBranchesCompleted_ShouldReturn100PercentAndIsFullyCompleted()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, reportingService, _) = CreateServices(dbContext);
        await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: Day1);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == ActivityModuleConstants.RffoActivityCode);
        var activeBranches = await dbContext.Branches.Where(b => b.IsActive).ToListAsync();

        foreach (var branch in activeBranches)
        {
            dbContext.ActivityStatuses.Add(new ActivityStatus
            {
                ActivityId = activity.ActivityId,
                BranchId = branch.BranchId,
                ReportingDate = Day1,
                CurrentStatus = BranchActivityStatus.Completed,
                LastUpdatedByDisplayName = "Test Submitter"
            });
        }
        await dbContext.SaveChangesAsync();

        var report = await reportingService.GetActivitySummaryReportAsync(new ReportingFilterQuery
        {
            ActivityCode = ActivityModuleConstants.RffoActivityCode,
            ReportingDate = Day1
        });

        Assert.Equal(5, report.TotalBranches);
        Assert.Equal(5, report.CompletedBranches);
        Assert.Equal(0, report.SubmittedPendingBranches);
        Assert.Equal(0, report.NotReportedBranches);
        Assert.Equal(0, report.PendingBranches);
        Assert.Equal(100.00m, report.CompletionPercentage);
        Assert.False(report.IsEmptyPopulation);
        Assert.True(report.IsFullyCompleted);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Reporting_MixedStatuses_ShouldCalculateExactCountsAndPercentages()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, reportingService, _) = CreateServices(dbContext);
        await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: Day1);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == ActivityModuleConstants.RffoActivityCode);
        var b3601 = await dbContext.Branches.SingleAsync(b => b.BranchCode == "36:01");
        var b3602 = await dbContext.Branches.SingleAsync(b => b.BranchCode == "36:02");
        var b3604 = await dbContext.Branches.SingleAsync(b => b.BranchCode == "36:04");

        // 36:01 -> Completed
        dbContext.ActivityStatuses.Add(new ActivityStatus
        {
            ActivityId = activity.ActivityId,
            BranchId = b3601.BranchId,
            ReportingDate = Day1,
            CurrentStatus = BranchActivityStatus.Completed
        });

        // 36:04 -> Completed
        dbContext.ActivityStatuses.Add(new ActivityStatus
        {
            ActivityId = activity.ActivityId,
            BranchId = b3604.BranchId,
            ReportingDate = Day1,
            CurrentStatus = BranchActivityStatus.Completed
        });

        // 36:02 -> Pending
        dbContext.ActivityStatuses.Add(new ActivityStatus
        {
            ActivityId = activity.ActivityId,
            BranchId = b3602.BranchId,
            ReportingDate = Day1,
            CurrentStatus = BranchActivityStatus.Pending
        });

        // 36:03 and 36:05 have no status rows -> NotReported
        await dbContext.SaveChangesAsync();

        var report = await reportingService.GetActivitySummaryReportAsync(new ReportingFilterQuery
        {
            ActivityCode = ActivityModuleConstants.RffoActivityCode,
            ReportingDate = Day1
        });

        Assert.Equal(5, report.TotalBranches);
        Assert.Equal(2, report.CompletedBranches);
        Assert.Equal(1, report.SubmittedPendingBranches);
        Assert.Equal(2, report.NotReportedBranches);
        Assert.Equal(3, report.PendingBranches); // 1 + 2 = 3
        Assert.Equal(40.00m, report.CompletionPercentage); // 2 / 5 * 100 = 40.00%
        Assert.False(report.IsEmptyPopulation);
        Assert.False(report.IsFullyCompleted);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Reporting_ZeroActiveBranches_ShouldReturnZeroPercentAndExplicitEmptyPopulation()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, reportingService, controller) = CreateServices(dbContext);
        await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: Day1);

        // Query date prior to hierarchy effective date -> zero branches
        var day0 = Day1.AddDays(-10);

        var report = await reportingService.GetActivitySummaryReportAsync(new ReportingFilterQuery
        {
            ActivityCode = ActivityModuleConstants.RffoActivityCode,
            ReportingDate = day0
        });

        Assert.Equal(0, report.TotalBranches);
        Assert.Equal(0, report.CompletedBranches);
        Assert.Equal(0, report.SubmittedPendingBranches);
        Assert.Equal(0, report.NotReportedBranches);
        Assert.Equal(0, report.PendingBranches);
        Assert.Equal(0.00m, report.CompletionPercentage);
        Assert.True(report.IsEmptyPopulation);
        Assert.False(report.IsFullyCompleted);

        // Also via API controller
        var actionResult = await controller.GetSummary(
            activityCode: "RFFO",
            reportingDate: day0);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var apiReport = Assert.IsType<ActivityReportSummaryDto>(okResult.Value);
        Assert.True(apiReport.IsEmptyPopulation);
        Assert.Equal(0.00m, apiReport.CompletionPercentage);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Reporting_StateAndRegionSummaries_ShouldFilterCorrectly()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, reportingService, _) = CreateServices(dbContext);
        await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: Day1);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == ActivityModuleConstants.RffoActivityCode);
        var b3601 = await dbContext.Branches.SingleAsync(b => b.BranchCode == "36:01");
        dbContext.ActivityStatuses.Add(new ActivityStatus
        {
            ActivityId = activity.ActivityId,
            BranchId = b3601.BranchId,
            ReportingDate = Day1,
            CurrentStatus = BranchActivityStatus.Completed
        });
        await dbContext.SaveChangesAsync();

        // 1. Zone summaries
        var zones = await reportingService.GetZoneSummariesAsync(new ReportingFilterQuery
        {
            ActivityCode = ActivityModuleConstants.RffoActivityCode,
            ReportingDate = Day1
        });
        Assert.Equal(2, zones.Count);
        var eastZone = Assert.Single(zones, z => z.ZoneCode == "EAST");
        var westZone = Assert.Single(zones, z => z.ZoneCode == "WEST");
        Assert.Equal(3, eastZone.TotalBranches); // 36:01, 36:02, 36:03
        Assert.Equal(1, eastZone.CompletedBranches);
        Assert.Equal(2, westZone.TotalBranches); // 36:04, 36:05

        // 2. State summaries with filter
        var states = await reportingService.GetStateSummariesAsync(new ReportingFilterQuery
        {
            ActivityCode = ActivityModuleConstants.RffoActivityCode,
            ReportingDate = Day1,
            ZoneCode = "EAST"
        });
        var odState = Assert.Single(states);
        Assert.Equal("OD", odState.StateCode);
        Assert.Equal(3, odState.TotalBranches);
        Assert.Equal(1, odState.CompletedBranches);

        // 3. Region summaries
        var regions = await reportingService.GetRegionSummariesAsync(new ReportingFilterQuery
        {
            ActivityCode = ActivityModuleConstants.RffoActivityCode,
            ReportingDate = Day1,
            StateCode = "OD"
        });
        Assert.Equal(2, regions.Count);
        var regA = Assert.Single(regions, r => r.RegionCode == "REG-A");
        var regB = Assert.Single(regions, r => r.RegionCode == "REG-B");
        Assert.Equal(2, regA.TotalBranches);
        Assert.Equal(1, regA.CompletedBranches);
        Assert.Equal(1, regB.TotalBranches);
        Assert.Equal(0, regB.CompletedBranches);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Reporting_ParentChildTotalsReconciliation_ShouldReconcileExactlyAcrossAllTiers()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, reportingService, _) = CreateServices(dbContext);
        await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: Day1);

        var activity = await dbContext.Activities.SingleAsync(a => a.ActivityCode == ActivityModuleConstants.RffoActivityCode);
        var b3601 = await dbContext.Branches.SingleAsync(b => b.BranchCode == "36:01");
        var b3604 = await dbContext.Branches.SingleAsync(b => b.BranchCode == "36:04");
        var b3605 = await dbContext.Branches.SingleAsync(b => b.BranchCode == "36:05");

        dbContext.ActivityStatuses.Add(new ActivityStatus
        {
            ActivityId = activity.ActivityId,
            BranchId = b3601.BranchId,
            ReportingDate = Day1,
            CurrentStatus = BranchActivityStatus.Completed
        });
        dbContext.ActivityStatuses.Add(new ActivityStatus
        {
            ActivityId = activity.ActivityId,
            BranchId = b3604.BranchId,
            ReportingDate = Day1,
            CurrentStatus = BranchActivityStatus.Completed
        });
        dbContext.ActivityStatuses.Add(new ActivityStatus
        {
            ActivityId = activity.ActivityId,
            BranchId = b3605.BranchId,
            ReportingDate = Day1,
            CurrentStatus = BranchActivityStatus.Pending
        });
        await dbContext.SaveChangesAsync();

        var query = new ReportingFilterQuery
        {
            ActivityCode = ActivityModuleConstants.RffoActivityCode,
            ReportingDate = Day1
        };

        var summary = await reportingService.GetActivitySummaryReportAsync(query);
        var zones = await reportingService.GetZoneSummariesAsync(query);
        var states = await reportingService.GetStateSummariesAsync(query);
        var regions = await reportingService.GetRegionSummariesAsync(query);
        var branches = await reportingService.GetBranchReportDetailsAsync(query);

        // Total branches reconciliation
        Assert.Equal(summary.TotalBranches, zones.Sum(z => z.TotalBranches));
        Assert.Equal(summary.TotalBranches, states.Sum(s => s.TotalBranches));
        Assert.Equal(summary.TotalBranches, regions.Sum(r => r.TotalBranches));
        Assert.Equal(summary.TotalBranches, branches.TotalMatchingBranches);

        // Completed branches reconciliation
        Assert.Equal(summary.CompletedBranches, zones.Sum(z => z.CompletedBranches));
        Assert.Equal(summary.CompletedBranches, states.Sum(s => s.CompletedBranches));
        Assert.Equal(summary.CompletedBranches, regions.Sum(r => r.CompletedBranches));
        Assert.Equal(summary.CompletedBranches, branches.Branches.Count(b => b.CurrentStatus == BranchActivityStatus.Completed));

        // Submitted pending reconciliation
        Assert.Equal(summary.SubmittedPendingBranches, zones.Sum(z => z.SubmittedPendingBranches));
        Assert.Equal(summary.SubmittedPendingBranches, states.Sum(s => s.SubmittedPendingBranches));
        Assert.Equal(summary.SubmittedPendingBranches, regions.Sum(r => r.SubmittedPendingBranches));
        Assert.Equal(summary.SubmittedPendingBranches, branches.Branches.Count(b => b.CurrentStatus == BranchActivityStatus.Pending));

        // Not reported reconciliation
        Assert.Equal(summary.NotReportedBranches, zones.Sum(z => z.NotReportedBranches));
        Assert.Equal(summary.NotReportedBranches, states.Sum(s => s.NotReportedBranches));
        Assert.Equal(summary.NotReportedBranches, regions.Sum(r => r.NotReportedBranches));
        Assert.Equal(summary.NotReportedBranches, branches.Branches.Count(b => b.CurrentStatus == BranchActivityStatus.NotReported));

        // Pending count reconciliation (Section 2 invariant: Pending count = Pending + NotReported)
        Assert.Equal(summary.PendingBranches, zones.Sum(z => z.PendingBranches));
        Assert.Equal(summary.PendingBranches, states.Sum(s => s.PendingBranches));
        Assert.Equal(summary.PendingBranches, regions.Sum(r => r.PendingBranches));

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Reporting_HistoricalBranchMapping_ShouldRespectEffectiveDatesAcrossDays()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, reportingService, _) = CreateServices(dbContext);
        await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: Day1);

        // On Day2, move branch 36:02 from REG-A to REG-B
        var day2UpdateBatch = new[]
        {
            new OrganisationImportRow(
                RowNumber: 2,
                ZoneCode: "EAST",
                ZoneName: "East Zone",
                StateCode: "OD",
                StateName: "Odisha",
                RegionCode: "REG-B", // Moved to REG-B
                RegionName: "Region B",
                BranchCode: "36:02",
                BranchName: "Cuttack City",
                IsZoneActive: true,
                IsStateActive: true,
                IsRegionActive: true,
                IsBranchActive: true)
        };
        await orgService.ImportHierarchyAsync(day2UpdateBatch, effectiveFromDate: Day2);

        // Day1 report: 36:02 must still be under REG-A
        var day1Regions = await reportingService.GetRegionSummariesAsync(new ReportingFilterQuery
        {
            ActivityCode = ActivityModuleConstants.RffoActivityCode,
            ReportingDate = Day1,
            StateCode = "OD"
        });
        var day1RegA = Assert.Single(day1Regions, r => r.RegionCode == "REG-A");
        var day1RegB = Assert.Single(day1Regions, r => r.RegionCode == "REG-B");
        Assert.Equal(2, day1RegA.TotalBranches); // 36:01, 36:02
        Assert.Equal(1, day1RegB.TotalBranches); // 36:03

        // Day2 report: 36:02 must now be under REG-B
        var day2Regions = await reportingService.GetRegionSummariesAsync(new ReportingFilterQuery
        {
            ActivityCode = ActivityModuleConstants.RffoActivityCode,
            ReportingDate = Day2,
            StateCode = "OD"
        });
        var day2RegA = Assert.Single(day2Regions, r => r.RegionCode == "REG-A");
        var day2RegB = Assert.Single(day2Regions, r => r.RegionCode == "REG-B");
        Assert.Equal(1, day2RegA.TotalBranches); // 36:01
        Assert.Equal(2, day2RegB.TotalBranches); // 36:02, 36:03

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Reporting_NoDuplicateBranchCounting_EvenWithMultipleHistoricalMappings()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, reportingService, _) = CreateServices(dbContext);
        await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: Day1);

        // Move 36:02 on Day2, creating a second mapping row
        var day2UpdateBatch = new[]
        {
            new OrganisationImportRow(
                RowNumber: 2,
                ZoneCode: "EAST",
                ZoneName: "East Zone",
                StateCode: "OD",
                StateName: "Odisha",
                RegionCode: "REG-B",
                RegionName: "Region B",
                BranchCode: "36:02",
                BranchName: "Cuttack City",
                IsZoneActive: true,
                IsStateActive: true,
                IsRegionActive: true,
                IsBranchActive: true)
        };
        await orgService.ImportHierarchyAsync(day2UpdateBatch, effectiveFromDate: Day2);

        // Verify total active branches on Day1 is exactly 5 (no duplicates)
        var day1Summary = await reportingService.GetActivitySummaryReportAsync(new ReportingFilterQuery
        {
            ActivityCode = ActivityModuleConstants.RffoActivityCode,
            ReportingDate = Day1
        });
        Assert.Equal(5, day1Summary.TotalBranches);

        // Verify total active branches on Day2 is also exactly 5 (no duplicates)
        var day2Summary = await reportingService.GetActivitySummaryReportAsync(new ReportingFilterQuery
        {
            ActivityCode = ActivityModuleConstants.RffoActivityCode,
            ReportingDate = Day2
        });
        Assert.Equal(5, day2Summary.TotalBranches);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Reporting_ReadOnlyReportingBehaviour_ShouldPerformZeroDatabaseMutations()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, reportingService, controller) = CreateServices(dbContext);
        await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: Day1);

        int statusCountBefore = await dbContext.ActivityStatuses.CountAsync();
        int historyCountBefore = await dbContext.ActivityHistories.CountAsync();
        int branchCountBefore = await dbContext.Branches.CountAsync();
        int summaryLogCountBefore = await dbContext.SummaryLogs.CountAsync();

        // Call all reporting endpoints
        var query = new ReportingFilterQuery
        {
            ActivityCode = ActivityModuleConstants.RffoActivityCode,
            ReportingDate = Day1
        };

        await reportingService.GetActivitySummaryReportAsync(query);
        await reportingService.GetZoneSummariesAsync(query);
        await reportingService.GetStateSummariesAsync(query);
        await reportingService.GetRegionSummariesAsync(query);
        await reportingService.GetBranchReportDetailsAsync(query);

        await controller.GetSummary(activityCode: "RFFO", reportingDate: Day1);
        await controller.GetZones(activityCode: "RFFO", reportingDate: Day1);
        await controller.GetStates(activityCode: "RFFO", reportingDate: Day1);
        await controller.GetRegions(activityCode: "RFFO", reportingDate: Day1);
        await controller.GetBranches(activityCode: "RFFO", reportingDate: Day1);

        int statusCountAfter = await dbContext.ActivityStatuses.CountAsync();
        int historyCountAfter = await dbContext.ActivityHistories.CountAsync();
        int branchCountAfter = await dbContext.Branches.CountAsync();
        int summaryLogCountAfter = await dbContext.SummaryLogs.CountAsync();

        Assert.Equal(statusCountBefore, statusCountAfter);
        Assert.Equal(historyCountBefore, historyCountAfter);
        Assert.Equal(branchCountBefore, branchCountAfter);
        Assert.Equal(summaryLogCountBefore, summaryLogCountAfter);

        Assert.False(dbContext.ChangeTracker.HasChanges());

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Reporting_InvalidActivityAndReportingDate_ShouldReturnAppropriateErrors()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (_, reportingService, controller) = CreateServices(dbContext);

        // 1. Unknown activity code -> KeyNotFoundException / 404
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            reportingService.GetActivitySummaryReportAsync(new ReportingFilterQuery
            {
                ActivityCode = "UNKNOWN_CODE",
                ReportingDate = Day1
            }));

        var notFoundResult = await controller.GetSummary(activityCode: "UNKNOWN_CODE", reportingDate: Day1);
        var notFound = Assert.IsType<NotFoundObjectResult>(notFoundResult.Result);
        var problem = Assert.IsType<ProblemDetails>(notFound.Value);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);

        // 2. Invalid activity code characters -> ArgumentException / 400
        var badRequestResult = await controller.GetSummary(activityCode: "BAD CODE$$", reportingDate: Day1);
        var badRequest = Assert.IsType<BadRequestObjectResult>(badRequestResult.Result);
        var badProblem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, badProblem.Status);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Reporting_ApiAuthorisation_ShouldEnforceRoleAndAuthenticationRules()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, reportingService, _) = CreateServices(dbContext);
        await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: Day1);

        // 1. Unauthenticated -> 401 Unauthorized
        var anonController = new ReportingController(reportingService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal(null, null, isAuthenticated: false) }
            }
        };
        var anonResult = await anonController.GetSummary(activityCode: "RFFO", reportingDate: Day1);
        Assert.IsType<UnauthorizedResult>(anonResult.Result);

        // 2. Viewer role -> 200 OK
        var viewerController = new ReportingController(reportingService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal("viewer.user", SystemRoles.Viewer) }
            }
        };
        var viewerResult = await viewerController.GetSummary(activityCode: "RFFO", reportingDate: Day1);
        Assert.IsType<OkObjectResult>(viewerResult.Result);

        // 3. Admin role -> 200 OK
        var adminController = new ReportingController(reportingService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal("admin.user", SystemRoles.Admin) }
            }
        };
        var adminResult = await adminController.GetSummary(activityCode: "RFFO", reportingDate: Day1);
        Assert.IsType<OkObjectResult>(adminResult.Result);

        // 4. SuperAdmin role -> 200 OK
        var superAdminController = new ReportingController(reportingService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal("superadmin.user", SystemRoles.SuperAdmin) }
            }
        };
        var superAdminResult = await superAdminController.GetSummary(activityCode: "RFFO", reportingDate: Day1);
        Assert.IsType<OkObjectResult>(superAdminResult.Result);

        // 5. Operator role without Viewer/Admin/SuperAdmin -> 403 Forbidden
        var operatorController = new ReportingController(reportingService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal("operator.user", "FieldOperator") }
            }
        };
        var operatorResult = await operatorController.GetSummary(activityCode: "RFFO", reportingDate: Day1);
        Assert.IsType<ForbidResult>(operatorResult.Result);

        await transaction.RollbackAsync();
    }
}
