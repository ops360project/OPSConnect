using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Reporting;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Organisation;
using Xunit;

namespace OpsConnect.UnitTests;

public class ReportingAggregationFoundationTests
{
    [Fact]
    public void ReportingFilterQuery_Normalize_ShouldEnforceDefaultsAndSanitize()
    {
        var raw = new ReportingFilterQuery
        {
            ActivityCode = "  rffo  ",
            ZoneCode = "  wz  ",
            StateCode = "  mh  ",
            RegionCode = "  pun  ",
            BranchCode = "  36:01  ",
            SearchTerm = "  Main Branch  ",
            Page = -5,
            PageSize = 999
        };

        var normalized = raw.Normalize();

        Assert.Equal("RFFO", normalized.ActivityCode);
        Assert.Equal("WZ", normalized.ZoneCode);
        Assert.Equal("MH", normalized.StateCode);
        Assert.Equal("PUN", normalized.RegionCode);
        Assert.Equal("36:01", normalized.BranchCode);
        Assert.Equal("Main Branch", normalized.SearchTerm);
        Assert.Null(normalized.Page);
        Assert.Equal(500, normalized.PageSize); // Clamped to 500 max
    }

    [Fact]
    public void ReportingFilterQuery_Normalize_EmptyActivityCode_ShouldDefaultToRffo()
    {
        var query = new ReportingFilterQuery { ActivityCode = "" }.Normalize();
        Assert.Equal(ActivityModuleConstants.RffoActivityCode, query.ActivityCode);
    }

    [Theory]
    [InlineData(0, 0, 0.00, true, false)]
    [InlineData(0, 10, 0.00, false, false)]
    [InlineData(10, 10, 100.00, false, true)]
    [InlineData(1, 3, 33.33, false, false)]
    [InlineData(2, 3, 66.67, false, false)]
    [InlineData(1, 6, 16.67, false, false)]
    public void ActivityReportSummaryDto_MetricInvariants_ShouldMatchSpecification(
        int completed,
        int total,
        decimal expectedPercentage,
        bool expectedIsEmpty,
        bool expectedIsFullyCompleted)
    {
        int submittedPending = total > completed ? 1 : 0;
        int notReported = total - completed - submittedPending;
        int pendingCount = submittedPending + notReported;

        decimal calculatedPct = total > 0
            ? Math.Round((decimal)completed * 100m / total, 2, MidpointRounding.AwayFromZero)
            : 0.00m;

        bool isEmpty = total == 0;
        bool isCompleted = !isEmpty && completed == total;

        Assert.Equal(expectedPercentage, calculatedPct);
        Assert.Equal(expectedIsEmpty, isEmpty);
        Assert.Equal(expectedIsFullyCompleted, isCompleted);
        Assert.Equal(total, completed + pendingCount);
        Assert.Equal(pendingCount, submittedPending + notReported);
    }

    [Fact]
    public void ActivityReportSummaryDto_PendingCount_ShouldEqualSubmittedPendingPlusNotReported()
    {
        var summary = new ActivityReportSummaryDto(
            ActivityId: 1,
            ActivityCode: "RFFO",
            ActivityName: "Request for Field Force Operations",
            ReportingDate: new DateOnly(2026, 11, 10),
            TimeZoneId: "Asia/Kolkata",
            GeneratedAtUtc: DateTime.UtcNow,
            TotalBranches: 100,
            CompletedBranches: 60,
            SubmittedPendingBranches: 15,
            NotReportedBranches: 25,
            PendingBranches: 40, // 15 + 25
            CompletionPercentage: 60.00m,
            IsEmptyPopulation: false,
            IsFullyCompleted: false,
            TotalZones: 2,
            TotalStates: 4,
            TotalRegions: 8,
            Zones: Array.Empty<ZoneReportSummaryDto>());

        // Section 2: Pending count = Pending + NotReported
        Assert.Equal(40, summary.PendingCount);
        Assert.Equal(summary.SubmittedPendingBranches + summary.NotReportedBranches, summary.PendingCount);
        Assert.Equal(summary.TotalBranches, summary.CompletedBranches + summary.PendingCount);
    }

    [Fact]
    public void HierarchySummaries_ParentChildReconciliation_ShouldReconcileExactly()
    {
        // 2 Regions under 1 State
        var region1 = new RegionReportSummaryDto(
            RegionId: 1, RegionCode: "PUN", RegionName: "Pune",
            StateId: 1, StateCode: "MH", StateName: "Maharashtra",
            ZoneId: 1, ZoneCode: "WZ", ZoneName: "West Zone",
            TotalBranches: 10, CompletedBranches: 4, SubmittedPendingBranches: 2, NotReportedBranches: 4,
            PendingBranches: 6, CompletionPercentage: 40.00m, IsEmptyPopulation: false, IsFullyCompleted: false);

        var region2 = new RegionReportSummaryDto(
            RegionId: 2, RegionCode: "MUM", RegionName: "Mumbai",
            StateId: 1, StateCode: "MH", StateName: "Maharashtra",
            ZoneId: 1, ZoneCode: "WZ", ZoneName: "West Zone",
            TotalBranches: 15, CompletedBranches: 11, SubmittedPendingBranches: 1, NotReportedBranches: 3,
            PendingBranches: 4, CompletionPercentage: 73.33m, IsEmptyPopulation: false, IsFullyCompleted: false);

        var stateRegions = new List<RegionReportSummaryDto> { region1, region2 };

        int stateTotal = stateRegions.Sum(r => r.TotalBranches);
        int stateCompleted = stateRegions.Sum(r => r.CompletedBranches);
        int stateSubmittedPending = stateRegions.Sum(r => r.SubmittedPendingBranches);
        int stateNotReported = stateRegions.Sum(r => r.NotReportedBranches);
        int statePending = stateRegions.Sum(r => r.PendingBranches);

        var state = new StateReportSummaryDto(
            StateId: 1, StateCode: "MH", StateName: "Maharashtra",
            ZoneId: 1, ZoneCode: "WZ", ZoneName: "West Zone",
            TotalBranches: stateTotal,
            CompletedBranches: stateCompleted,
            SubmittedPendingBranches: stateSubmittedPending,
            NotReportedBranches: stateNotReported,
            PendingBranches: statePending,
            CompletionPercentage: Math.Round((decimal)stateCompleted * 100m / stateTotal, 2, MidpointRounding.AwayFromZero),
            IsEmptyPopulation: false,
            IsFullyCompleted: false,
            Regions: stateRegions);

        Assert.Equal(25, state.TotalBranches);
        Assert.Equal(15, state.CompletedBranches);
        Assert.Equal(3, state.SubmittedPendingBranches);
        Assert.Equal(7, state.NotReportedBranches);
        Assert.Equal(10, state.PendingBranches);
        Assert.Equal(60.00m, state.CompletionPercentage);

        // Zone containing State
        var zoneStates = new List<StateReportSummaryDto> { state };
        int zoneTotal = zoneStates.Sum(s => s.TotalBranches);
        int zoneCompleted = zoneStates.Sum(s => s.CompletedBranches);
        int zoneSubmittedPending = zoneStates.Sum(s => s.SubmittedPendingBranches);
        int zoneNotReported = zoneStates.Sum(s => s.NotReportedBranches);
        int zonePending = zoneStates.Sum(s => s.PendingBranches);

        var zone = new ZoneReportSummaryDto(
            ZoneId: 1, ZoneCode: "WZ", ZoneName: "West Zone",
            TotalBranches: zoneTotal,
            CompletedBranches: zoneCompleted,
            SubmittedPendingBranches: zoneSubmittedPending,
            NotReportedBranches: zoneNotReported,
            PendingBranches: zonePending,
            CompletionPercentage: Math.Round((decimal)zoneCompleted * 100m / zoneTotal, 2, MidpointRounding.AwayFromZero),
            IsEmptyPopulation: false,
            IsFullyCompleted: false,
            States: zoneStates);

        Assert.Equal(25, zone.TotalBranches);
        Assert.Equal(15, zone.CompletedBranches);
        Assert.Equal(10, zone.PendingBranches);
        Assert.Equal(60.00m, zone.CompletionPercentage);
    }
}
