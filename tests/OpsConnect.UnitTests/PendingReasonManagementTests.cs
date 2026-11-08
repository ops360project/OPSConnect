using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Reporting;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using Xunit;

namespace OpsConnect.UnitTests;

/// <summary>
/// Unit tests for Task 7.1: Standardized Pending Reason Management.
/// Covers:
/// 1. Valid Pending submission with a reason
/// 2. Pending submission without a reason
/// 3. Invalid reason code
/// 4. Other without description
/// 5. Completed submission without a reason
/// 6. Pending reason stored in ActivityStatus
/// 7. Reason preserved in ActivityHistory
/// 8. Reason updated correctly after another Pending submission
/// 9. Completed transition and history preservation
/// 12. Telegram report formatting with pending reasons
/// </summary>
public class PendingReasonManagementTests
{
    private readonly TelegramActivityMessageParser _parser = new();
    private readonly TelegramReportFormatter _formatter = new();

    // =========================================================================
    // Scenario 1: Valid Pending submission with a reason
    // =========================================================================

    [Theory]
    [InlineData("CashShortage", PendingReasonCode.CashShortage, "Cash Shortage")]
    [InlineData("Cash Shortage", PendingReasonCode.CashShortage, "Cash Shortage")]
    [InlineData("cash_shortage", PendingReasonCode.CashShortage, "Cash Shortage")]
    [InlineData("SystemOutage", PendingReasonCode.SystemOutage, "System Outage")]
    [InlineData("CustomerDelay", PendingReasonCode.CustomerDelay, "Customer Delay")]
    [InlineData("FieldExecutiveOnLeave", PendingReasonCode.FieldExecutiveOnLeave, "Field Executive On Leave")]
    [InlineData("OperationalIssue", PendingReasonCode.OperationalIssue, "Operational Issue")]
    public void Parser_ValidPendingSubmission_WithStandardReason_ShouldSucceed(
        string reasonInput,
        PendingReasonCode expectedCode,
        string expectedDisplayName)
    {
        string message = $"""
            Activity- RFFO
            BranchID- 36:01
            Status- Pending
            Reason- {reasonInput}
            """;

        var result = _parser.Parse(message);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ParsedMessage);
        Assert.Equal("RFFO", result.ParsedMessage!.ActivityCode);
        Assert.Equal("36:01", result.ParsedMessage.BranchCode);
        Assert.Equal(BranchActivityStatus.Pending, result.ParsedMessage.Status);
        Assert.Equal(expectedCode, result.ParsedMessage.PendingReason);
        Assert.Equal(expectedDisplayName, result.ParsedMessage.PendingReason!.Value.ToDisplayName());
    }

    [Fact]
    public void Parser_ValidPendingSubmission_WithOptionalDescription_ShouldSucceed()
    {
        const string message = """
            Activity- RFFO
            BranchID- 36:01
            Status- Pending
            Reason- CashShortage
            Description- Hub dispatch van delayed by traffic
            """;

        var result = _parser.Parse(message);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ParsedMessage);
        Assert.Equal(PendingReasonCode.CashShortage, result.ParsedMessage!.PendingReason);
        Assert.Equal("Hub dispatch van delayed by traffic", result.ParsedMessage.PendingReasonDescription);
    }

    // =========================================================================
    // Scenario 2: Pending submission without a reason
    // =========================================================================

    [Fact]
    public void Parser_PendingSubmission_WithoutReason_ShouldFailWithMissingPendingReason()
    {
        const string message = """
            Activity- RFFO
            BranchID- 36:01
            Status- Pending
            """;

        var result = _parser.Parse(message);

        Assert.False(result.IsSuccess);
        Assert.Null(result.ParsedMessage);
        var error = Assert.Single(result.Errors);
        Assert.Equal(TelegramActivityMessageParser.ReasonFieldName, error.FieldName);
        Assert.Equal(TelegramMessageParseErrorCode.MissingPendingReason, error.ErrorCode);
    }

    [Fact]
    public void Command_PendingSubmission_WithoutReason_ShouldThrowArgumentException()
    {
        var command = new ActivityUpdateSubmissionCommand(
            ActivityCode: "RFFO",
            BranchCode: "36:01",
            IncomingStatus: BranchActivityStatus.Pending,
            PendingReason: null);

        var ex = Assert.Throws<ArgumentException>(() => command.ValidateAndNormalize());
        Assert.Contains("PendingReason is required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Scenario 3: Invalid reason code
    // =========================================================================

    [Theory]
    [InlineData("UnknownReasonCode")]
    [InlineData("NetworkFailure")]
    [InlineData("Holiday")]
    [InlineData("12345")]
    public void Parser_PendingSubmission_WithInvalidReasonCode_ShouldFailWithInvalidPendingReason(string invalidReason)
    {
        string message = $"""
            Activity- RFFO
            BranchID- 36:01
            Status- Pending
            Reason- {invalidReason}
            """;

        var result = _parser.Parse(message);

        Assert.False(result.IsSuccess);
        Assert.Null(result.ParsedMessage);
        var error = Assert.Single(result.Errors);
        Assert.Equal(TelegramActivityMessageParser.ReasonFieldName, error.FieldName);
        Assert.Equal(TelegramMessageParseErrorCode.InvalidPendingReason, error.ErrorCode);
    }

    // =========================================================================
    // Scenario 4: Other without description
    // =========================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parser_PendingSubmission_WithReasonOther_WithoutDescription_ShouldFailWithMissingDescription(string? descLine)
    {
        string message = descLine == null
            ? """
              Activity- RFFO
              BranchID- 36:01
              Status- Pending
              Reason- Other
              """
            : $"""
              Activity- RFFO
              BranchID- 36:01
              Status- Pending
              Reason- Other
              Description- {descLine}
              """;

        var result = _parser.Parse(message);

        Assert.False(result.IsSuccess);
        Assert.Null(result.ParsedMessage);
        var error = Assert.Single(result.Errors);
        Assert.Equal(TelegramActivityMessageParser.DescriptionFieldName, error.FieldName);
        Assert.Equal(TelegramMessageParseErrorCode.MissingPendingReasonDescription, error.ErrorCode);
    }

    [Fact]
    public void Parser_PendingSubmission_WithReasonOther_WithValidDescription_ShouldSucceed()
    {
        const string message = """
            Activity- RFFO
            BranchID- 36:01
            Status- Pending
            Reason- Other
            Description- Local flood preventing field team movement
            """;

        var result = _parser.Parse(message);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ParsedMessage);
        Assert.Equal(PendingReasonCode.Other, result.ParsedMessage!.PendingReason);
        Assert.Equal("Local flood preventing field team movement", result.ParsedMessage.PendingReasonDescription);
    }

    [Fact]
    public void Parser_PendingSubmission_WithDescriptionExceeding500Chars_ShouldFailWithTooLong()
    {
        string longDesc = new('X', 501);
        string message = $"""
            Activity- RFFO
            BranchID- 36:01
            Status- Pending
            Reason- Other
            Description- {longDesc}
            """;

        var result = _parser.Parse(message);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal(TelegramActivityMessageParser.DescriptionFieldName, error.FieldName);
        Assert.Equal(TelegramMessageParseErrorCode.PendingReasonDescriptionTooLong, error.ErrorCode);
    }

    // =========================================================================
    // Scenario 5: Completed submission without a reason
    // =========================================================================

    [Fact]
    public void Parser_CompletedSubmission_WithoutReason_ShouldSucceedCleanly()
    {
        const string message = """
            Activity- RFFO
            BranchID- 36:01
            Status- Completed
            """;

        var result = _parser.Parse(message);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ParsedMessage);
        Assert.Equal(BranchActivityStatus.Completed, result.ParsedMessage!.Status);
        Assert.Null(result.ParsedMessage.PendingReason);
        Assert.Null(result.ParsedMessage.PendingReasonDescription);
    }

    [Fact]
    public void Command_CompletedSubmission_WithoutReason_ShouldValidateAndNormalizeCleanly()
    {
        var command = new ActivityUpdateSubmissionCommand(
            ActivityCode: "RFFO",
            BranchCode: "36:01",
            IncomingStatus: BranchActivityStatus.Completed);

        var normalized = command.ValidateAndNormalize();

        Assert.Equal(BranchActivityStatus.Completed, normalized.IncomingStatus);
        Assert.Null(normalized.PendingReason);
        Assert.Null(normalized.PendingReasonDescription);
    }

    // =========================================================================
    // Scenario 6 & 7: Pending reason stored in ActivityStatus and preserved in ActivityHistory
    // =========================================================================

    [Fact]
    public void ApplyTransition_InitialPendingSubmission_ShouldStorePendingReasonOnActivityStatus()
    {
        var status = new ActivityStatus
        {
            ActivityId = 1,
            BranchId = 101,
            ReportingDate = new DateOnly(2026, 11, 8),
            CurrentStatus = BranchActivityStatus.NotReported
        };

        var updateTime = new DateTime(2026, 11, 8, 4, 30, 0, DateTimeKind.Utc);
        var transitionResult = status.ApplyTransition(
            incomingStatus: BranchActivityStatus.Pending,
            currentReportingDate: new DateOnly(2026, 11, 8),
            telegramMessageLogId: 67890,
            telegramUserId: 12345,
            senderDisplayName: "Field Operator",
            occurredAtUtc: updateTime,
            reopenPermission: null,
            pendingReason: PendingReasonCode.CashShortage,
            pendingReasonDescription: "Cash transit delayed");

        Assert.True(transitionResult.IsAccepted);
        Assert.Equal(TransitionOutcome.Accepted, transitionResult.Outcome);
        Assert.Null(transitionResult.RejectionReason);
        Assert.Equal(BranchActivityStatus.Pending, status.CurrentStatus);
        Assert.Equal(PendingReasonCode.CashShortage, status.CurrentPendingReason);
        Assert.Equal("Cash transit delayed", status.CurrentPendingReasonDescription);
        Assert.Equal("Field Operator", status.LastUpdatedByDisplayName);

        // Verify history entity can be constructed from these properties
        var history = new ActivityHistory
        {
            ActivityId = status.ActivityId,
            BranchId = status.BranchId,
            ReportingDate = status.ReportingDate,
            PreviousStatus = BranchActivityStatus.NotReported,
            NewStatus = BranchActivityStatus.Pending,
            Outcome = transitionResult.Outcome,
            PendingReason = PendingReasonCode.CashShortage,
            PendingReasonDescription = "Cash transit delayed",
            RecordedAtUtc = updateTime,
            TelegramDisplayName = "Field Operator"
        };

        Assert.Equal(PendingReasonCode.CashShortage, history.PendingReason);
        Assert.Equal("Cash transit delayed", history.PendingReasonDescription);
    }

    // =========================================================================
    // Scenario 8: Reason updated correctly after another Pending submission
    // =========================================================================

    [Fact]
    public void ApplyTransition_ConsecutivePendingSubmissions_ShouldUpdateReasonAndReturnAcceptedUnchanged()
    {
        var status = new ActivityStatus
        {
            ActivityId = 1,
            BranchId = 101,
            ReportingDate = new DateOnly(2026, 11, 8),
            CurrentStatus = BranchActivityStatus.Pending,
            CurrentPendingReason = PendingReasonCode.CashShortage,
            CurrentPendingReasonDescription = "Initial shortage"
        };

        var updateTime2 = new DateTime(2026, 11, 8, 5, 0, 0, DateTimeKind.Utc);
        var transitionResult = status.ApplyTransition(
            incomingStatus: BranchActivityStatus.Pending,
            currentReportingDate: new DateOnly(2026, 11, 8),
            telegramMessageLogId: 67891,
            telegramUserId: 12345,
            senderDisplayName: "Field Operator",
            occurredAtUtc: updateTime2,
            reopenPermission: null,
            pendingReason: PendingReasonCode.CustomerDelay,
            pendingReasonDescription: "Customer requested afternoon visit");

        Assert.True(transitionResult.IsAccepted);
        Assert.Equal(TransitionOutcome.AcceptedUnchanged, transitionResult.Outcome);
        Assert.False(transitionResult.DidStatusChange);
        Assert.Null(transitionResult.RejectionReason);
        Assert.Equal(BranchActivityStatus.Pending, status.CurrentStatus);
        // Reason must be updated to the latest submitted reason
        Assert.Equal(PendingReasonCode.CustomerDelay, status.CurrentPendingReason);
        Assert.Equal("Customer requested afternoon visit", status.CurrentPendingReasonDescription);
        Assert.Equal(updateTime2, status.UpdatedAtUtc);
    }

    // =========================================================================
    // Scenario 9: Completed transition clears current reason and preserves history
    // =========================================================================

    [Fact]
    public void ApplyTransition_PendingToCompleted_ShouldClearCurrentReasonOnStatus()
    {
        var status = new ActivityStatus
        {
            ActivityId = 1,
            BranchId = 101,
            ReportingDate = new DateOnly(2026, 11, 8),
            CurrentStatus = BranchActivityStatus.Pending,
            CurrentPendingReason = PendingReasonCode.OperationalIssue,
            CurrentPendingReasonDescription = "Power grid fault"
        };

        var completeTime = new DateTime(2026, 11, 8, 6, 0, 0, DateTimeKind.Utc);
        var transitionResult = status.ApplyTransition(
            incomingStatus: BranchActivityStatus.Completed,
            currentReportingDate: new DateOnly(2026, 11, 8),
            telegramMessageLogId: 67892,
            telegramUserId: 12345,
            senderDisplayName: "Field Operator",
            occurredAtUtc: completeTime,
            reopenPermission: null,
            pendingReason: null,
            pendingReasonDescription: null);

        Assert.True(transitionResult.IsAccepted);
        Assert.Equal(TransitionOutcome.Accepted, transitionResult.Outcome);
        Assert.True(transitionResult.DidStatusChange);
        Assert.Null(transitionResult.RejectionReason);
        Assert.Equal(BranchActivityStatus.Completed, status.CurrentStatus);
        // Completed status must reset current pending reason to null
        Assert.Null(status.CurrentPendingReason);
        Assert.Null(status.CurrentPendingReasonDescription);

        // Previous history entry retains original reason; new history has null
        var completedHistory = new ActivityHistory
        {
            ActivityId = status.ActivityId,
            BranchId = status.BranchId,
            ReportingDate = status.ReportingDate,
            PreviousStatus = BranchActivityStatus.Pending,
            NewStatus = BranchActivityStatus.Completed,
            Outcome = transitionResult.Outcome,
            PendingReason = null,
            PendingReasonDescription = null,
            RecordedAtUtc = completeTime
        };

        Assert.Null(completedHistory.PendingReason);
        Assert.Null(completedHistory.PendingReasonDescription);
    }

    // =========================================================================
    // Scenario 12: Telegram report formatting with pending reasons
    // =========================================================================

    [Fact]
    public void TelegramFormatter_WithSubmittedPendingBranches_ShouldIncludePendingReasonsSection()
    {
        var reasons = new List<PendingReasonCountDto>
        {
            new(PendingReasonCode.CashShortage, "Cash Shortage", 12, 60.00m),
            new(PendingReasonCode.SystemOutage, "System Outage", 8, 40.00m)
        };

        var summary = new ActivityReportSummaryDto(
            ActivityId: 1,
            ActivityCode: "RFFO",
            ActivityName: "Request for Field Force Operations",
            ReportingDate: new DateOnly(2026, 11, 8),
            TimeZoneId: "Asia/Kolkata",
            GeneratedAtUtc: new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc),
            TotalBranches: 100,
            CompletedBranches: 75,
            SubmittedPendingBranches: 20,
            NotReportedBranches: 5,
            PendingBranches: 25,
            CompletionPercentage: 75.00m,
            IsEmptyPopulation: false,
            IsFullyCompleted: false,
            TotalZones: 1,
            TotalStates: 1,
            TotalRegions: 1,
            Zones: Array.Empty<ZoneReportSummaryDto>(),
            PendingReasons: reasons);

        var report = _formatter.FormatActivitySummary(summary);
        var msg = report.PrimaryMessage;

        Assert.Contains("Pending Reasons:", msg);
        Assert.Contains("• Cash Shortage: 12 (60.00%)", msg);
        Assert.Contains("• System Outage: 8 (40.00%)", msg);
    }

    [Fact]
    public void TelegramFormatter_WithZeroSubmittedPendingBranches_ShouldOmitPendingReasonsSection()
    {
        var summary = new ActivityReportSummaryDto(
            ActivityId: 1,
            ActivityCode: "RFFO",
            ActivityName: "Request for Field Force Operations",
            ReportingDate: new DateOnly(2026, 11, 8),
            TimeZoneId: "Asia/Kolkata",
            GeneratedAtUtc: new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc),
            TotalBranches: 100,
            CompletedBranches: 100,
            SubmittedPendingBranches: 0,
            NotReportedBranches: 0,
            PendingBranches: 0,
            CompletionPercentage: 100.00m,
            IsEmptyPopulation: false,
            IsFullyCompleted: true,
            TotalZones: 0,
            TotalStates: 0,
            TotalRegions: 0,
            Zones: Array.Empty<ZoneReportSummaryDto>(),
            PendingReasons: Array.Empty<PendingReasonCountDto>());

        var report = _formatter.FormatActivitySummary(summary);
        var msg = report.PrimaryMessage;

        Assert.DoesNotContain("Pending Reasons:", msg);
    }

    [Fact]
    public void TelegramFormatter_WithBranchDetails_ShouldDisplayPendingReasonNextToBranch()
    {
        var branchDetails = new List<BranchReportDetailDto>
        {
            new(
                BranchId: 101,
                BranchCode: "36:01",
                BranchName: "Bhubaneswar Main",
                ZoneId: 1,
                ZoneCode: "EAST",
                ZoneName: "East Zone",
                StateId: 1,
                StateCode: "OD",
                StateName: "Odisha",
                RegionId: 1,
                RegionCode: "BHUB",
                RegionName: "Bhubaneswar",
                ReportingDate: new DateOnly(2026, 11, 8),
                CurrentStatus: BranchActivityStatus.Pending,
                IsDerivedNotReported: false,
                ReopenedCount: 0,
                HasAvailableReopenPermission: false,
                LastTelegramMessageLogId: 1001,
                LastUpdatedByTelegramUserId: 9901,
                LastUpdatedByDisplayName: "Operator 1",
                LastUpdatedAtUtc: new DateTime(2026, 11, 8, 4, 30, 0, DateTimeKind.Utc),
                PendingReason: PendingReasonCode.CashShortage,
                PendingReasonDescription: "Cash shortage from currency chest"),
            new(
                BranchId: 102,
                BranchCode: "36:02",
                BranchName: "Cuttack City",
                ZoneId: 1,
                ZoneCode: "EAST",
                ZoneName: "East Zone",
                StateId: 1,
                StateCode: "OD",
                StateName: "Odisha",
                RegionId: 1,
                RegionCode: "BHUB",
                RegionName: "Bhubaneswar",
                ReportingDate: new DateOnly(2026, 11, 8),
                CurrentStatus: BranchActivityStatus.NotReported,
                IsDerivedNotReported: true,
                ReopenedCount: 0,
                HasAvailableReopenPermission: false,
                LastTelegramMessageLogId: null,
                LastUpdatedByTelegramUserId: null,
                LastUpdatedByDisplayName: null,
                LastUpdatedAtUtc: null,
                PendingReason: null,
                PendingReasonDescription: null)
        };

        var regions = new List<RegionReportSummaryDto>
        {
            new(
                RegionId: 1, RegionCode: "BHUB", RegionName: "Bhubaneswar",
                StateId: 1, StateCode: "OD", StateName: "Odisha",
                ZoneId: 1, ZoneCode: "EAST", ZoneName: "East Zone",
                TotalBranches: 2, CompletedBranches: 0, SubmittedPendingBranches: 1, NotReportedBranches: 1,
                PendingBranches: 2, CompletionPercentage: 0.00m, IsEmptyPopulation: false, IsFullyCompleted: false)
        };

        var states = new List<StateReportSummaryDto>
        {
            new(
                StateId: 1, StateCode: "OD", StateName: "Odisha",
                ZoneId: 1, ZoneCode: "EAST", ZoneName: "East Zone",
                TotalBranches: 2, CompletedBranches: 0, SubmittedPendingBranches: 1, NotReportedBranches: 1,
                PendingBranches: 2, CompletionPercentage: 0.00m, IsEmptyPopulation: false, IsFullyCompleted: false,
                Regions: regions)
        };

        var zones = new List<ZoneReportSummaryDto>
        {
            new(
                ZoneId: 1, ZoneCode: "EAST", ZoneName: "East Zone",
                TotalBranches: 2, CompletedBranches: 0, SubmittedPendingBranches: 1, NotReportedBranches: 1,
                PendingBranches: 2, CompletionPercentage: 0.00m, IsEmptyPopulation: false, IsFullyCompleted: false,
                States: states)
        };

        var summary = new ActivityReportSummaryDto(
            ActivityId: 1,
            ActivityCode: "RFFO",
            ActivityName: "Request for Field Force Operations",
            ReportingDate: new DateOnly(2026, 11, 8),
            TimeZoneId: "Asia/Kolkata",
            GeneratedAtUtc: new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc),
            TotalBranches: 2,
            CompletedBranches: 0,
            SubmittedPendingBranches: 1,
            NotReportedBranches: 1,
            PendingBranches: 2,
            CompletionPercentage: 0.00m,
            IsEmptyPopulation: false,
            IsFullyCompleted: false,
            TotalZones: 1,
            TotalStates: 1,
            TotalRegions: 1,
            Zones: zones);

        var report = _formatter.FormatActivitySummary(
            summary,
            branches: branchDetails,
            options: new TelegramReportFormattingOptions
            {
                DetailLevel = TelegramReportDetailLevel.FullBranchDetails,
                UseHtmlMarkup = false
            });

        var msg = report.PrimaryMessage;
        Assert.Contains("36:01 - Bhubaneswar Main: Pending (Cash Shortage)", msg);
        Assert.Contains("36:02 - Cuttack City: Not Reported", msg);
    }
}
