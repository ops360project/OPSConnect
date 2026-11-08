using OpsConnect.Application.Reporting;
using OpsConnect.Domain.Activities;
using Xunit;

namespace OpsConnect.UnitTests;

public class TelegramReportFormatterTests
{
    private readonly ITelegramReportFormatter _formatter = new TelegramReportFormatter();

    private static ActivityReportSummaryDto CreateSampleSummary(
        string activityCode = "RFFO",
        string activityName = "Request for Field Force Operations",
        int total = 120,
        int completed = 85,
        int submittedPending = 20,
        int notReported = 15,
        decimal completionPercentage = 70.83m,
        bool isEmpty = false,
        bool isCompleted = false)
    {
        var regions = new List<RegionReportSummaryDto>
        {
            new(
                RegionId: 10, RegionCode: "BHUB", RegionName: "Bhubaneswar",
                StateId: 2, StateCode: "OD", StateName: "Odisha",
                ZoneId: 1, ZoneCode: "EAST", ZoneName: "East Zone",
                TotalBranches: 22, CompletedBranches: 15, SubmittedPendingBranches: 5, NotReportedBranches: 2,
                PendingBranches: 7, CompletionPercentage: 68.18m, IsEmptyPopulation: false, IsFullyCompleted: false)
        };

        var states = new List<StateReportSummaryDto>
        {
            new(
                StateId: 2, StateCode: "OD", StateName: "Odisha",
                ZoneId: 1, ZoneCode: "EAST", ZoneName: "East Zone",
                TotalBranches: 60, CompletedBranches: 45, SubmittedPendingBranches: 10, NotReportedBranches: 5,
                PendingBranches: 15, CompletionPercentage: 75.00m, IsEmptyPopulation: false, IsFullyCompleted: false,
                Regions: regions)
        };

        var zones = new List<ZoneReportSummaryDto>
        {
            new(
                ZoneId: 1, ZoneCode: "EAST", ZoneName: "East Zone",
                TotalBranches: 120, CompletedBranches: 85, SubmittedPendingBranches: 20, NotReportedBranches: 15,
                PendingBranches: 35, CompletionPercentage: 70.83m, IsEmptyPopulation: false, IsFullyCompleted: false,
                States: states)
        };

        return new ActivityReportSummaryDto(
            ActivityId: 1,
            ActivityCode: activityCode,
            ActivityName: activityName,
            ReportingDate: new DateOnly(2026, 11, 10),
            TimeZoneId: "Asia/Kolkata",
            GeneratedAtUtc: new DateTime(2026, 11, 10, 13, 0, 0, DateTimeKind.Utc), // 18:30 IST
            TotalBranches: total,
            CompletedBranches: completed,
            SubmittedPendingBranches: submittedPending,
            NotReportedBranches: notReported,
            PendingBranches: submittedPending + notReported,
            CompletionPercentage: completionPercentage,
            IsEmptyPopulation: isEmpty,
            IsFullyCompleted: isCompleted,
            TotalZones: zones.Count,
            TotalStates: states.Count,
            TotalRegions: regions.Count,
            Zones: zones);
    }

    [Fact]
    public void FormatActivitySummary_ShouldDisplayCorrectHeaderTitleAndDates()
    {
        var summary = CreateSampleSummary();
        var report = _formatter.FormatActivitySummary(summary);

        Assert.Equal(1, report.MessageCount);
        var msg = report.PrimaryMessage;

        Assert.Contains("Request for Field Force Operations — Daily Status", msg);
        Assert.Contains("Reporting Date: 10-11-2026", msg);
        Assert.Contains("Generated: 10-11-2026 18:30:00 IST", msg);
    }

    [Fact]
    public void FormatActivitySummary_ShouldAccuratelyDisplayCountsAndDistinguishPendingFromNotReported()
    {
        var summary = CreateSampleSummary();
        var report = _formatter.FormatActivitySummary(summary);

        var msg = report.PrimaryMessage;

        Assert.Contains("Total Branches: 120", msg);
        Assert.Contains("Completed: 85", msg);
        Assert.Contains("Submitted Pending: 20", msg);
        Assert.Contains("Not Reported: 15", msg);
        Assert.Contains("Total Pending: 35", msg);
        Assert.Contains("Completion: 70.83%", msg);
        Assert.Contains("Status: In Progress", msg);
    }

    [Fact]
    public void FormatActivitySummary_EmptyPopulation_ShouldExplicitlyIdentifyZeroBranchesAndNotCompleted()
    {
        var summary = new ActivityReportSummaryDto(
            ActivityId: 1,
            ActivityCode: "RFFO",
            ActivityName: "Request for Field Force Operations",
            ReportingDate: new DateOnly(2026, 11, 10),
            TimeZoneId: "Asia/Kolkata",
            GeneratedAtUtc: new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc),
            TotalBranches: 0,
            CompletedBranches: 0,
            SubmittedPendingBranches: 0,
            NotReportedBranches: 0,
            PendingBranches: 0,
            CompletionPercentage: 0.00m,
            IsEmptyPopulation: true,
            IsFullyCompleted: false,
            TotalZones: 0,
            TotalStates: 0,
            TotalRegions: 0,
            Zones: Array.Empty<ZoneReportSummaryDto>());

        var report = _formatter.FormatActivitySummary(summary);
        var msg = report.PrimaryMessage;

        Assert.Contains("Total Branches: 0", msg);
        Assert.Contains("Completed: 0", msg);
        Assert.Contains("Completion: 0.00%", msg);
        Assert.Contains("Status: Empty Branch Population (0 mapped active branches)", msg);
        Assert.DoesNotContain("All Branches Completed", msg);
    }

    [Fact]
    public void FormatActivitySummary_FullyCompleted_ShouldIndicateAllBranchesCompleted()
    {
        var summary = CreateSampleSummary(
            total: 50,
            completed: 50,
            submittedPending: 0,
            notReported: 0,
            completionPercentage: 100.00m,
            isEmpty: false,
            isCompleted: true);

        var report = _formatter.FormatActivitySummary(summary);
        var msg = report.PrimaryMessage;

        Assert.Contains("Total Branches: 50", msg);
        Assert.Contains("Completed: 50", msg);
        Assert.Contains("Total Pending: 0", msg);
        Assert.Contains("Completion: 100.00%", msg);
        Assert.Contains("Status: All Branches Completed", msg);
    }

    [Fact]
    public void FormatActivitySummary_HierarchySummary_ShouldFormatZoneStateAndRegionBreakdowns()
    {
        var summary = CreateSampleSummary();
        var report = _formatter.FormatActivitySummary(summary);
        var msg = report.PrimaryMessage;

        Assert.Contains("Zone: East Zone (EAST)", msg);
        Assert.Contains("Completed: 85 | Pending: 20 | Not Reported: 15 | Total: 120 (70.83%)", msg);

        Assert.Contains("State: Odisha (OD)", msg);
        Assert.Contains("Completed: 45 | Pending: 10 | Not Reported: 5 | Total: 60 (75.00%)", msg);

        Assert.Contains("Region: Bhubaneswar (BHUB)", msg);
        Assert.Contains("Completed: 15 | Pending: 5 | Not Reported: 2 | Total: 22 (68.18%)", msg);
    }

    [Fact]
    public void FormatActivitySummary_FullBranchDetails_ShouldRenderBranchEntries()
    {
        var summary = CreateSampleSummary();
        var branches = new List<BranchReportDetailDto>
        {
            new(
                BranchId: 101, BranchCode: "36:01", BranchName: "Bhubaneswar Main",
                ZoneId: 1, ZoneCode: "EAST", ZoneName: "East Zone",
                StateId: 2, StateCode: "OD", StateName: "Odisha",
                RegionId: 10, RegionCode: "BHUB", RegionName: "Bhubaneswar",
                ReportingDate: new DateOnly(2026, 11, 10),
                CurrentStatus: BranchActivityStatus.Completed,
                IsDerivedNotReported: false, ReopenedCount: 0, HasAvailableReopenPermission: false,
                LastTelegramMessageLogId: 1, LastUpdatedByTelegramUserId: 123,
                LastUpdatedByDisplayName: "User1", LastUpdatedAtUtc: DateTime.UtcNow),
            new(
                BranchId: 102, BranchCode: "36:02", BranchName: "Cuttack City",
                ZoneId: 1, ZoneCode: "EAST", ZoneName: "East Zone",
                StateId: 2, StateCode: "OD", StateName: "Odisha",
                RegionId: 10, RegionCode: "BHUB", RegionName: "Bhubaneswar",
                ReportingDate: new DateOnly(2026, 11, 10),
                CurrentStatus: BranchActivityStatus.Pending,
                IsDerivedNotReported: false, ReopenedCount: 0, HasAvailableReopenPermission: false,
                LastTelegramMessageLogId: 2, LastUpdatedByTelegramUserId: 124,
                LastUpdatedByDisplayName: "User2", LastUpdatedAtUtc: DateTime.UtcNow),
            new(
                BranchId: 103, BranchCode: "36:03", BranchName: "Puri Beach",
                ZoneId: 1, ZoneCode: "EAST", ZoneName: "East Zone",
                StateId: 2, StateCode: "OD", StateName: "Odisha",
                RegionId: 10, RegionCode: "BHUB", RegionName: "Bhubaneswar",
                ReportingDate: new DateOnly(2026, 11, 10),
                CurrentStatus: BranchActivityStatus.NotReported,
                IsDerivedNotReported: true, ReopenedCount: 0, HasAvailableReopenPermission: false,
                LastTelegramMessageLogId: null, LastUpdatedByTelegramUserId: null,
                LastUpdatedByDisplayName: null, LastUpdatedAtUtc: null)
        };

        var report = _formatter.FormatBranchDetails(summary, branches);
        var msg = report.PrimaryMessage;

        Assert.Contains("Branches:", msg);
        Assert.Contains("• 36:01 - Bhubaneswar Main: Completed", msg);
        Assert.Contains("• 36:02 - Cuttack City: Pending", msg);
        Assert.Contains("• 36:03 - Puri Beach: Not Reported", msg);
    }

    [Fact]
    public void FormatActivitySummary_PendingOnlyFilter_ShouldExcludeCompletedBranches()
    {
        var summary = CreateSampleSummary();
        var branches = new List<BranchReportDetailDto>
        {
            new(
                BranchId: 101, BranchCode: "36:01", BranchName: "Bhubaneswar Main",
                ZoneId: 1, ZoneCode: "EAST", ZoneName: "East Zone",
                StateId: 2, StateCode: "OD", StateName: "Odisha",
                RegionId: 10, RegionCode: "BHUB", RegionName: "Bhubaneswar",
                ReportingDate: new DateOnly(2026, 11, 10),
                CurrentStatus: BranchActivityStatus.Completed,
                IsDerivedNotReported: false, ReopenedCount: 0, HasAvailableReopenPermission: false,
                LastTelegramMessageLogId: null, LastUpdatedByTelegramUserId: null,
                LastUpdatedByDisplayName: null, LastUpdatedAtUtc: null),
            new(
                BranchId: 102, BranchCode: "36:02", BranchName: "Cuttack City",
                ZoneId: 1, ZoneCode: "EAST", ZoneName: "East Zone",
                StateId: 2, StateCode: "OD", StateName: "Odisha",
                RegionId: 10, RegionCode: "BHUB", RegionName: "Bhubaneswar",
                ReportingDate: new DateOnly(2026, 11, 10),
                CurrentStatus: BranchActivityStatus.Pending,
                IsDerivedNotReported: false, ReopenedCount: 0, HasAvailableReopenPermission: false,
                LastTelegramMessageLogId: null, LastUpdatedByTelegramUserId: null,
                LastUpdatedByDisplayName: null, LastUpdatedAtUtc: null)
        };

        var options = new TelegramReportFormattingOptions
        {
            DetailLevel = TelegramReportDetailLevel.FullBranchDetails,
            ShowBranchDetailsForPendingOnly = true
        };

        var report = _formatter.FormatActivitySummary(summary, branches, options);
        var msg = report.PrimaryMessage;

        Assert.Contains("• 36:02 - Cuttack City: Pending", msg);
        Assert.DoesNotContain("• 36:01 - Bhubaneswar Main: Completed", msg);
    }

    [Fact]
    public void FormatActivitySummary_LongReportSplitting_ShouldCreateMultipleOrderedPartsWithoutOmissions()
    {
        var summary = CreateSampleSummary();

        // Generate 60 distinct branches under Region 10
        var branches = new List<BranchReportDetailDto>();
        for (int i = 1; i <= 60; i++)
        {
            branches.Add(new BranchReportDetailDto(
                BranchId: 100 + i,
                BranchCode: $"36:{i:D2}",
                BranchName: $"Branch Name Number {i} Extended Description for Size",
                ZoneId: 1, ZoneCode: "EAST", ZoneName: "East Zone",
                StateId: 2, StateCode: "OD", StateName: "Odisha",
                RegionId: 10, RegionCode: "BHUB", RegionName: "Bhubaneswar",
                ReportingDate: new DateOnly(2026, 11, 10),
                CurrentStatus: i % 2 == 0 ? BranchActivityStatus.Completed : BranchActivityStatus.Pending,
                IsDerivedNotReported: false, ReopenedCount: 0, HasAvailableReopenPermission: false,
                LastTelegramMessageLogId: null, LastUpdatedByTelegramUserId: null,
                LastUpdatedByDisplayName: null, LastUpdatedAtUtc: null));
        }

        // Test with a tight message limit (e.g. 700 chars) to force multiple parts
        var options = new TelegramReportFormattingOptions
        {
            DetailLevel = TelegramReportDetailLevel.FullBranchDetails,
            MaxMessageLength = 700
        };

        var report = _formatter.FormatActivitySummary(summary, branches, options);

        Assert.True(report.MessageCount > 1);

        // Every message part must be within MaxMessageLength
        for (int i = 0; i < report.MessageCount; i++)
        {
            var part = report.Messages[i];
            Assert.True(part.Length <= 700, $"Part {i + 1} length ({part.Length}) exceeded limit 700");

            // Part 1 has title with (Part 1/N)
            if (i == 0)
            {
                Assert.Contains($"(Part 1/{report.MessageCount})", part);
            }
            else
            {
                // Continuation parts have continuation header
                Assert.Contains($"(Part {i + 1}/{report.MessageCount})", part);
                Assert.Contains("Reporting Date: 10-11-2026", part);
            }
        }

        // Verify zero omitted branches across all messages
        var fullReportText = string.Join("\n", report.Messages);
        for (int i = 1; i <= 60; i++)
        {
            Assert.Contains($"36:{i:D2}", fullReportText);
        }
    }

    [Fact]
    public void TelegramMarkupEscaping_HtmlAndMarkdownV2_ShouldCorrectlyEscapeSpecialCharacters()
    {
        string rawText = "Zone <East & West> [Main]*Branch_Code~#1!";

        string escapedHtml = TelegramReportFormatter.EscapeHtml(rawText);
        Assert.Equal("Zone &lt;East &amp; West&gt; [Main]*Branch_Code~#1!", escapedHtml);

        string escapedMarkdown = TelegramReportFormatter.EscapeMarkdownV2(rawText);
        Assert.Equal(@"Zone <East & West\> \[Main\]\*Branch\_Code\~\#1\!", escapedMarkdown);
    }

    [Fact]
    public void FormatActivitySummary_SpecialCharactersInNames_ShouldNotBreakFormatting()
    {
        var summary = CreateSampleSummary(
            activityName: "Operations <East & West> Special Edition",
            total: 10,
            completed: 5,
            submittedPending: 3,
            notReported: 2,
            completionPercentage: 50.00m);

        // Plain text mode
        var plainReport = _formatter.FormatActivitySummary(summary, options: new TelegramReportFormattingOptions { UseHtmlMarkup = false });
        Assert.Contains("Operations <East & West> Special Edition", plainReport.PrimaryMessage);

        // HTML mode
        var htmlReport = _formatter.FormatActivitySummary(summary, options: new TelegramReportFormattingOptions { UseHtmlMarkup = true });
        Assert.Contains("Operations &lt;East &amp; West&gt; Special Edition", htmlReport.PrimaryMessage);
    }

    [Fact]
    public void FormatActivitySummary_FutureActivityNames_GetDataAndCash_ShouldFormatGenerically()
    {
        // 1. GetData activity
        var getDataSummary = CreateSampleSummary(
            activityCode: "GETDATA",
            activityName: "Get Data Operational Sync",
            total: 200,
            completed: 180,
            submittedPending: 15,
            notReported: 5,
            completionPercentage: 90.00m);

        var getDataReport = _formatter.FormatActivitySummary(getDataSummary);
        Assert.Equal("GETDATA", getDataReport.ActivityCode);
        Assert.Contains("Get Data Operational Sync — Daily Status", getDataReport.PrimaryMessage);
        Assert.Contains("Completion: 90.00%", getDataReport.PrimaryMessage);

        // 2. Cash activity
        var cashSummary = CreateSampleSummary(
            activityCode: "CASH_RECON",
            activityName: "Daily Cash Reconciliation",
            total: 50,
            completed: 50,
            submittedPending: 0,
            notReported: 0,
            completionPercentage: 100.00m,
            isCompleted: true);

        var cashReport = _formatter.FormatActivitySummary(cashSummary);
        Assert.Equal("CASH_RECON", cashReport.ActivityCode);
        Assert.Contains("Daily Cash Reconciliation — Daily Status", cashReport.PrimaryMessage);
        Assert.Contains("Status: All Branches Completed", cashReport.PrimaryMessage);
    }

    [Fact]
    public void IndividualHierarchyFormatters_ShouldReturnExpectedSingleBlockStrings()
    {
        var summary = CreateSampleSummary();
        var zone = summary.Zones[0];
        var state = zone.States![0];
        var region = state.Regions![0];

        string zoneText = _formatter.FormatZoneSummary(zone);
        Assert.StartsWith("Zone: East Zone (EAST)", zoneText);
        Assert.Contains("Completed: 85", zoneText);

        string stateText = _formatter.FormatStateSummary(state);
        Assert.StartsWith("State: Odisha (OD)", stateText);
        Assert.Contains("Completed: 45", stateText);

        string regionText = _formatter.FormatRegionSummary(region);
        Assert.StartsWith("Region: Bhubaneswar (BHUB)", regionText);
        Assert.Contains("Completed: 15", regionText);
    }
}
