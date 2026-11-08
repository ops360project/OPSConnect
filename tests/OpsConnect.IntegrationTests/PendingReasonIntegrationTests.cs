using Microsoft.EntityFrameworkCore;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Activities.RFFO;
using OpsConnect.Application.Common;
using OpsConnect.Application.Organisation;
using OpsConnect.Application.Reporting;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Organisation;
using OpsConnect.Domain.Telegram;
using OpsConnect.Infrastructure.Persistence;
using OpsConnect.Infrastructure.Services;
using Xunit;

namespace OpsConnect.IntegrationTests;

/// <summary>
/// Integration tests verifying database persistence and reporting aggregation for Task 7.1:
/// - Scenario 6: Pending reason stored in ActivityStatus
/// - Scenario 7: Reason preserved in ActivityHistory
/// - Scenario 8: Reason updated correctly after another Pending submission
/// - Scenario 9: Completed transition clears current reason and preserves history
/// - Scenario 10: Overall reason-wise aggregation (PendingReasons breakdown)
/// - Scenario 11: State and region reason-wise aggregation
/// </summary>
public class PendingReasonIntegrationTests
{
    private static readonly DateOnly TestDate = new(2026, 11, 8);

    private static OpsConnectDbContext CreateContext()
    {
        var factory = new OpsConnectDbContextFactory();
        return factory.CreateDbContext(Array.Empty<string>());
    }

    private static (
        IOrganisationMasterService OrgService,
        IRffoActivityUpdateHandler RffoHandler,
        IActivityUpdateProcessor Processor,
        IActivityStatusQueryService QueryService,
        IActivityReportingService ReportingService) CreateServices(OpsConnectDbContext context)
    {
        var clock = new SystemBusinessClock();
        var orgService = new OrganisationMasterService(
            context,
            new OrganisationExcelReader(),
            new OrganisationHierarchyImportValidator(),
            clock);
        var rffoHandler = new RffoActivityUpdateHandler(context, clock);
        var processor = new ActivityUpdateProcessor(
            new TelegramActivityMessageParser(),
            new IActivityUpdateHandler[] { rffoHandler });
        var queryService = new ActivityStatusQueryService(context, clock);
        var reportingService = new ActivityReportingService(context, clock);

        return (orgService, rffoHandler, processor, queryService, reportingService);
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
            IsBranchActive: true)
    ];

    [Fact]
    public async Task PendingReason_StoredInStatusAndPreservedInHistory_AndUpdatedOnSubsequentPending()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, processor, queryService, _) = CreateServices(dbContext);
        await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: TestDate);

        // 1. Initial Pending submission with CashShortage
        const string initialMessage = """
            Activity- RFFO
            BranchID- 36:01
            Status- Pending
            Reason- CashShortage
            Description- Transit delay from currency chest
            """;

        var result1 = await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: initialMessage,
            TelegramMessageLogId: 1001,
            TelegramUserId: 9901,
            SenderDisplayName: "Bhubaneswar Operator",
            ExplicitReportingDate: TestDate));

        Assert.True(result1.IsAccepted);
        Assert.Equal(TransitionOutcome.Accepted, result1.Outcome);

        // Verify ActivityStatus in DB (Scenario 6)
        var statusInDb = await dbContext.ActivityStatuses
            .Include(s => s.Branch)
            .SingleAsync(s => s.Branch.BranchCode == "36:01" && s.ReportingDate == TestDate);

        Assert.Equal(BranchActivityStatus.Pending, statusInDb.CurrentStatus);
        Assert.Equal(PendingReasonCode.CashShortage, statusInDb.CurrentPendingReason);
        Assert.Equal("Transit delay from currency chest", statusInDb.CurrentPendingReasonDescription);

        // Verify ActivityHistory in DB (Scenario 7)
        var historyEntries = await dbContext.ActivityHistories
            .Where(h => h.ActivityId == statusInDb.ActivityId && h.BranchId == statusInDb.BranchId && h.ReportingDate == TestDate)
            .OrderBy(h => h.RecordedAtUtc)
            .ToListAsync();

        var history1 = Assert.Single(historyEntries);
        Assert.Equal(BranchActivityStatus.NotReported, history1.PreviousStatus);
        Assert.Equal(BranchActivityStatus.Pending, history1.NewStatus);
        Assert.Equal(TransitionOutcome.Accepted, history1.Outcome);
        Assert.Equal(PendingReasonCode.CashShortage, history1.PendingReason);
        Assert.Equal("Transit delay from currency chest", history1.PendingReasonDescription);

        // Verify QueryService returns mapped reasons
        var statusDto = await queryService.GetBranchCurrentStatusAsync("RFFO", "36:01", TestDate);
        Assert.NotNull(statusDto);
        Assert.Equal(BranchActivityStatus.Pending, statusDto.CurrentStatus);
        Assert.Equal(PendingReasonCode.CashShortage, statusDto.CurrentPendingReason);
        Assert.Equal("Transit delay from currency chest", statusDto.CurrentPendingReasonDescription);

        // 2. Subsequent Pending submission with CustomerDelay (Scenario 8)
        const string subsequentMessage = """
            Activity- RFFO
            BranchID- 36:01
            Status- Pending
            Reason- CustomerDelay
            Description- Key customer requested afternoon appointment
            """;

        var result2 = await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: subsequentMessage,
            TelegramMessageLogId: 1002,
            TelegramUserId: 9901,
            SenderDisplayName: "Bhubaneswar Operator",
            ExplicitReportingDate: TestDate));

        Assert.True(result2.IsAccepted);
        Assert.Equal(TransitionOutcome.AcceptedUnchanged, result2.Outcome);

        // Verify ActivityStatus updated in DB
        await dbContext.Entry(statusInDb).ReloadAsync();
        Assert.Equal(BranchActivityStatus.Pending, statusInDb.CurrentStatus);
        Assert.Equal(PendingReasonCode.CustomerDelay, statusInDb.CurrentPendingReason);
        Assert.Equal("Key customer requested afternoon appointment", statusInDb.CurrentPendingReasonDescription);

        // Verify ActivityHistory now has 2 rows
        historyEntries = await dbContext.ActivityHistories
            .Where(h => h.ActivityId == statusInDb.ActivityId && h.BranchId == statusInDb.BranchId && h.ReportingDate == TestDate)
            .OrderBy(h => h.ActivityHistoryId)
            .ToListAsync();

        Assert.Equal(2, historyEntries.Count);
        Assert.Equal(PendingReasonCode.CashShortage, historyEntries[0].PendingReason);
        Assert.Equal("Transit delay from currency chest", historyEntries[0].PendingReasonDescription);
        Assert.Equal(TransitionOutcome.Accepted, historyEntries[0].Outcome);

        Assert.Equal(PendingReasonCode.CustomerDelay, historyEntries[1].PendingReason);
        Assert.Equal("Key customer requested afternoon appointment", historyEntries[1].PendingReasonDescription);
        Assert.Equal(TransitionOutcome.AcceptedUnchanged, historyEntries[1].Outcome);

        // 3. Completed submission (Scenario 9)
        const string completedMessage = """
            Activity- RFFO
            BranchID- 36:01
            Status- Completed
            """;

        var result3 = await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: completedMessage,
            TelegramMessageLogId: 1003,
            TelegramUserId: 9901,
            SenderDisplayName: "Bhubaneswar Operator",
            ExplicitReportingDate: TestDate));

        Assert.True(result3.IsAccepted);
        Assert.Equal(TransitionOutcome.Accepted, result3.Outcome);

        // Verify ActivityStatus has reset reasons to null
        await dbContext.Entry(statusInDb).ReloadAsync();
        Assert.Equal(BranchActivityStatus.Completed, statusInDb.CurrentStatus);
        Assert.Null(statusInDb.CurrentPendingReason);
        Assert.Null(statusInDb.CurrentPendingReasonDescription);

        // Verify ActivityHistory preserves all 3 entries intact
        historyEntries = await dbContext.ActivityHistories
            .Where(h => h.ActivityId == statusInDb.ActivityId && h.BranchId == statusInDb.BranchId && h.ReportingDate == TestDate)
            .OrderBy(h => h.ActivityHistoryId)
            .ToListAsync();

        Assert.Equal(3, historyEntries.Count);
        // History 1: initial pending with CashShortage
        Assert.Equal(PendingReasonCode.CashShortage, historyEntries[0].PendingReason);
        // History 2: updated pending with CustomerDelay
        Assert.Equal(PendingReasonCode.CustomerDelay, historyEntries[1].PendingReason);
        // History 3: completed with null reason
        Assert.Equal(BranchActivityStatus.Completed, historyEntries[2].NewStatus);
        Assert.Null(historyEntries[2].PendingReason);
        Assert.Null(historyEntries[2].PendingReasonDescription);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task PendingReason_ReportingAggregation_OverallStateAndRegionBreakdowns()
    {
        await using var dbContext = CreateContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var (orgService, _, processor, _, reportingService) = CreateServices(dbContext);
        await orgService.ImportHierarchyAsync(CreateTestHierarchyBatch(), effectiveFromDate: TestDate);

        // Setup 4 pending branches with varied reasons and 1 completed branch
        // 36:01 (East / OD / REG-A) -> CashShortage
        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: "Activity- RFFO\nBranchID- 36:01\nStatus- Pending\nReason- CashShortage",
            ExplicitReportingDate: TestDate));

        // 36:02 (East / OD / REG-A) -> CashShortage
        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: "Activity- RFFO\nBranchID- 36:02\nStatus- Pending\nReason- CashShortage",
            ExplicitReportingDate: TestDate));

        // 36:03 (East / OD / REG-B) -> SystemOutage
        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: "Activity- RFFO\nBranchID- 36:03\nStatus- Pending\nReason- SystemOutage",
            ExplicitReportingDate: TestDate));

        // 36:04 (West / MH / REG-W) -> Other with description
        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: "Activity- RFFO\nBranchID- 36:04\nStatus- Pending\nReason- Other\nDescription- Local strike",
            ExplicitReportingDate: TestDate));

        // 36:05 (West / MH / REG-W) -> Completed
        await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: "Activity- RFFO\nBranchID- 36:05\nStatus- Completed",
            ExplicitReportingDate: TestDate));

        var query = new ReportingFilterQuery
        {
            ActivityCode = ActivityModuleConstants.RffoActivityCode,
            ReportingDate = TestDate
        };

        // 1. Overall Activity Summary Report (Scenario 10)
        var summaryReport = await reportingService.GetActivitySummaryReportAsync(query);
        Assert.Equal(5, summaryReport.TotalBranches);
        Assert.Equal(1, summaryReport.CompletedBranches);
        Assert.Equal(4, summaryReport.SubmittedPendingBranches);
        Assert.Equal(0, summaryReport.NotReportedBranches);

        Assert.NotNull(summaryReport.PendingReasons);
        Assert.Equal(3, summaryReport.PendingReasons.Count);

        // Sorted descending by branch count: CashShortage (2), then others (1 each)
        var topReason = summaryReport.PendingReasons[0];
        Assert.Equal(PendingReasonCode.CashShortage, topReason.ReasonCode);
        Assert.Equal("Cash Shortage", topReason.ReasonName);
        Assert.Equal(2, topReason.BranchCount);
        Assert.Equal(50.00m, topReason.PercentageOfPending); // 2 / 4 = 50.00%

        var otherReasons = summaryReport.PendingReasons.Skip(1).ToList();
        Assert.Contains(otherReasons, r => r.ReasonCode == PendingReasonCode.SystemOutage && r.BranchCount == 1 && r.PercentageOfPending == 25.00m);
        Assert.Contains(otherReasons, r => r.ReasonCode == PendingReasonCode.Other && r.BranchCount == 1 && r.PercentageOfPending == 25.00m);

        // Sum of reason branch counts must equal SubmittedPendingBranches
        Assert.Equal(summaryReport.SubmittedPendingBranches, summaryReport.PendingReasons.Sum(r => r.BranchCount));

        // 2. State Summaries (Scenario 11)
        var states = await reportingService.GetStateSummariesAsync(query);
        Assert.Equal(2, states.Count);

        var odState = Assert.Single(states, s => s.StateCode == "OD");
        Assert.Equal(3, odState.SubmittedPendingBranches);
        Assert.NotNull(odState.PendingReasons);
        Assert.Equal(2, odState.PendingReasons.Count);
        Assert.Contains(odState.PendingReasons, r => r.ReasonCode == PendingReasonCode.CashShortage && r.BranchCount == 2);
        Assert.Contains(odState.PendingReasons, r => r.ReasonCode == PendingReasonCode.SystemOutage && r.BranchCount == 1);
        Assert.Equal(odState.SubmittedPendingBranches, odState.PendingReasons.Sum(r => r.BranchCount));

        var mhState = Assert.Single(states, s => s.StateCode == "MH");
        Assert.Equal(1, mhState.SubmittedPendingBranches);
        Assert.NotNull(mhState.PendingReasons);
        var mhReason = Assert.Single(mhState.PendingReasons);
        Assert.Equal(PendingReasonCode.Other, mhReason.ReasonCode);
        Assert.Equal(1, mhReason.BranchCount);
        Assert.Equal(100.00m, mhReason.PercentageOfPending);

        // 3. Region Summaries (Scenario 11)
        var regions = await reportingService.GetRegionSummariesAsync(query);
        Assert.Equal(3, regions.Count);

        var regA = Assert.Single(regions, r => r.RegionCode == "REG-A");
        Assert.Equal(2, regA.SubmittedPendingBranches);
        Assert.NotNull(regA.PendingReasons);
        var regAReason = Assert.Single(regA.PendingReasons);
        Assert.Equal(PendingReasonCode.CashShortage, regAReason.ReasonCode);
        Assert.Equal(2, regAReason.BranchCount);
        Assert.Equal(100.00m, regAReason.PercentageOfPending);

        var regB = Assert.Single(regions, r => r.RegionCode == "REG-B");
        Assert.Equal(1, regB.SubmittedPendingBranches);
        Assert.NotNull(regB.PendingReasons);
        var regBReason = Assert.Single(regB.PendingReasons);
        Assert.Equal(PendingReasonCode.SystemOutage, regBReason.ReasonCode);
        Assert.Equal(1, regBReason.BranchCount);
        Assert.Equal(100.00m, regBReason.PercentageOfPending);

        var regW = Assert.Single(regions, r => r.RegionCode == "REG-W");
        Assert.Equal(1, regW.SubmittedPendingBranches);
        Assert.NotNull(regW.PendingReasons);
        var regWReason = Assert.Single(regW.PendingReasons);
        Assert.Equal(PendingReasonCode.Other, regWReason.ReasonCode);
        Assert.Equal(1, regWReason.BranchCount);
        Assert.Equal(100.00m, regWReason.PercentageOfPending);

        await transaction.RollbackAsync();
    }
}
