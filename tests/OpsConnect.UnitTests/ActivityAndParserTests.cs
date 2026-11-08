using OpsConnect.Application.Activities.Common;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;

namespace OpsConnect.UnitTests;

public class ActivityAndParserTests
{
    private readonly TelegramActivityMessageParser _parser = new();

    // =========================================================================
    // 1. Standalone Telegram Activity Message Parser Tests
    // =========================================================================

    [Fact]
    public void Parse_WithCanonicalPendingMessage_WithValidReason_ShouldReturnSuccessAndNormalizedFields()
    {
        const string message = """
            Activity- RFFO
            BranchID- 36:01
            Status- Pending
            Reason- CashShortage
            Description- Cash collection delayed from hub
            """;

        var result = _parser.Parse(message);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ParsedMessage);
        Assert.Empty(result.Errors);
        Assert.Null(result.PrimaryFailureReason);
        Assert.Equal("RFFO", result.ParsedMessage!.ActivityCode);
        Assert.Equal("36:01", result.ParsedMessage.BranchCode);
        Assert.Equal(BranchActivityStatus.Pending, result.ParsedMessage.Status);
        Assert.Equal(PendingReasonCode.CashShortage, result.ParsedMessage.PendingReason);
        Assert.Equal("Cash collection delayed from hub", result.ParsedMessage.PendingReasonDescription);
    }

    [Fact]
    public void Parse_WithPendingMessage_WithoutReason_ShouldReturnMissingPendingReasonError()
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
    public void Parse_WithPendingMessage_WithInvalidReasonCode_ShouldReturnInvalidPendingReasonError()
    {
        const string message = """
            Activity- RFFO
            BranchID- 36:01
            Status- Pending
            Reason- InvalidReasonXYZ
            """;

        var result = _parser.Parse(message);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal(TelegramActivityMessageParser.ReasonFieldName, error.FieldName);
        Assert.Equal(TelegramMessageParseErrorCode.InvalidPendingReason, error.ErrorCode);
    }

    [Fact]
    public void Parse_WithPendingMessage_WithReasonOther_WithoutDescription_ShouldReturnMissingDescriptionError()
    {
        const string message = """
            Activity- RFFO
            BranchID- 36:01
            Status- Pending
            Reason- Other
            """;

        var result = _parser.Parse(message);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal(TelegramActivityMessageParser.DescriptionFieldName, error.FieldName);
        Assert.Equal(TelegramMessageParseErrorCode.MissingPendingReasonDescription, error.ErrorCode);
    }

    [Fact]
    public void Parse_WithPendingMessage_WithReasonOther_WithValidDescription_ShouldSucceed()
    {
        const string message = """
            Activity- RFFO
            BranchID- 36:01
            Status- Pending
            Reason- Other
            Description- Local power outage affecting network switch
            """;

        var result = _parser.Parse(message);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ParsedMessage);
        Assert.Equal(PendingReasonCode.Other, result.ParsedMessage!.PendingReason);
        Assert.Equal("Local power outage affecting network switch", result.ParsedMessage.PendingReasonDescription);
    }

    [Fact]
    public void Parse_WithPendingMessage_WithDescriptionExceeding500Chars_ShouldReturnTooLongError()
    {
        string longDesc = new string('A', 501);
        string message = $"""
            Activity- RFFO
            BranchID- 36:01
            Status- Pending
            Reason- OperationalIssue
            Description- {longDesc}
            """;

        var result = _parser.Parse(message);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal(TelegramActivityMessageParser.DescriptionFieldName, error.FieldName);
        Assert.Equal(TelegramMessageParseErrorCode.PendingReasonDescriptionTooLong, error.ErrorCode);
    }

    [Theory]
    [InlineData("CashShortage", PendingReasonCode.CashShortage)]
    [InlineData("Cash Shortage", PendingReasonCode.CashShortage)]
    [InlineData("SystemOutage", PendingReasonCode.SystemOutage)]
    [InlineData("CustomerDelay", PendingReasonCode.CustomerDelay)]
    [InlineData("FieldExecutiveOnLeave", PendingReasonCode.FieldExecutiveOnLeave)]
    [InlineData("OperationalIssue", PendingReasonCode.OperationalIssue)]
    public void Parse_AllStandardPendingReasons_ShouldParseCorrectly(string rawReason, PendingReasonCode expected)
    {
        string message = $"""
            Activity- RFFO
            BranchID- 36:01
            Status- Pending
            Reason- {rawReason}
            """;

        var result = _parser.Parse(message);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ParsedMessage);
        Assert.Equal(expected, result.ParsedMessage!.PendingReason);
    }

    [Fact]
    public void Parse_WithCanonicalCompletedMessage_ShouldReturnSuccessAndNormalizedFields()
    {
        const string message = """
            Activity- RFFO
            BranchID- 36:01
            Status- Completed
            """;

        var result = _parser.Parse(message);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ParsedMessage);
        Assert.Empty(result.Errors);
        Assert.Equal("RFFO", result.ParsedMessage!.ActivityCode);
        Assert.Equal("36:01", result.ParsedMessage.BranchCode);
        Assert.Equal(BranchActivityStatus.Completed, result.ParsedMessage.Status);
    }

    [Fact]
    public void Parse_WithCasingWhitespaceBlankLinesAndColonSeparators_ShouldPreserveColonInsideBranchCode()
    {
        const string message = """

              branch_id :   36:01:b  

              STATUS  -   completed  
              activity :  rffo  

            """;

        var result = _parser.Parse(message);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ParsedMessage);
        Assert.Equal("RFFO", result.ParsedMessage!.ActivityCode);
        Assert.Equal("36:01:B", result.ParsedMessage.BranchCode);
        Assert.Equal(BranchActivityStatus.Completed, result.ParsedMessage.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n  \t  ")]
    public void Parse_WithEmptyOrWhitespaceMessage_ShouldReturnEmptyMessageError(string? rawMessage)
    {
        var result = _parser.Parse(rawMessage);

        Assert.False(result.IsSuccess);
        Assert.Null(result.ParsedMessage);
        var error = Assert.Single(result.Errors);
        Assert.Equal(TelegramMessageParseErrorCode.EmptyMessage, error.ErrorCode);
    }

    [Fact]
    public void Parse_WithMissingRequiredFields_ShouldReportEachMissingField()
    {
        const string message = "Activity- RFFO";

        var result = _parser.Parse(message);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, e =>
            e.FieldName == TelegramActivityMessageParser.BranchIdFieldName &&
            e.ErrorCode == TelegramMessageParseErrorCode.MissingRequiredField);
        Assert.Contains(result.Errors, e =>
            e.FieldName == TelegramActivityMessageParser.StatusFieldName &&
            e.ErrorCode == TelegramMessageParseErrorCode.MissingRequiredField);
    }

    [Fact]
    public void Parse_WithDuplicateIdenticalField_ShouldReturnDuplicateFieldError()
    {
        const string message = """
            Activity- RFFO
            BranchID- 36:01
            Branch code: 36:01
            Status- Completed
            """;

        var result = _parser.Parse(message);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal(3, error.LineNumber);
        Assert.Equal(TelegramActivityMessageParser.BranchIdFieldName, error.FieldName);
        Assert.Equal(TelegramMessageParseErrorCode.DuplicateField, error.ErrorCode);
    }

    [Fact]
    public void Parse_WithConflictingFieldValues_ShouldReturnConflictingFieldError()
    {
        const string message = """
            Activity- RFFO
            BranchID- 36:01
            Status- Completed
            Status- Pending
            """;

        var result = _parser.Parse(message);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal(4, error.LineNumber);
        Assert.Equal(TelegramActivityMessageParser.StatusFieldName, error.FieldName);
        Assert.Equal(TelegramMessageParseErrorCode.ConflictingField, error.ErrorCode);
    }

    [Theory]
    [InlineData("Activity- RFFO\nBranchID- 36:01\nStatus- Completed\nPlease update urgently")]
    [InlineData("Activity- RFFO\nBranchID- 36:01\nStatus- Completed\nNote- Done")]
    public void Parse_WithAmbiguousOrUnrecognisedLine_ShouldReturnUnrecognisedLineError(string message)
    {
        var result = _parser.Parse(message);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, e => e.ErrorCode == TelegramMessageParseErrorCode.UnrecognisedLine);
    }

    [Theory]
    [InlineData("36 : 01")]
    [InlineData(":01")]
    [InlineData("36:")]
    [InlineData("36::01")]
    [InlineData("36@01")]
    [InlineData("")]
    public void Parse_WithInvalidBranchCode_ShouldReturnInvalidBranchCodeError(string invalidBranchCode)
    {
        var message = $"""
            Activity- RFFO
            BranchID- {invalidBranchCode}
            Status- Completed
            """;

        var result = _parser.Parse(message);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal(TelegramActivityMessageParser.BranchIdFieldName, error.FieldName);
        Assert.Equal(TelegramMessageParseErrorCode.InvalidBranchCode, error.ErrorCode);
    }

    [Theory]
    [InlineData("NotReported")]
    [InlineData("Not_Reported")]
    [InlineData("InProgress")]
    [InlineData("Done")]
    [InlineData("")]
    public void Parse_WithUnsupportedOrNotReportedStatus_ShouldReturnUnsupportedStatusError(string unsupportedStatus)
    {
        var message = $"""
            Activity- RFFO
            BranchID- 36:01
            Status- {unsupportedStatus}
            """;

        var result = _parser.Parse(message);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal(TelegramActivityMessageParser.StatusFieldName, error.FieldName);
        Assert.Equal(TelegramMessageParseErrorCode.UnsupportedStatus, error.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("RFFO 1")]
    [InlineData("RFFO:1")]
    [InlineData("RFFO@")]
    public void Parse_WithMissingOrInvalidActivityCode_ShouldReturnInvalidActivityCodeError(string invalidActivity)
    {
        var message = $"""
            Activity- {invalidActivity}
            BranchID- 36:01
            Status- Completed
            """;

        var result = _parser.Parse(message);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal(TelegramActivityMessageParser.ActivityFieldName, error.FieldName);
        Assert.Equal(TelegramMessageParseErrorCode.InvalidActivityCode, error.ErrorCode);
    }

    // =========================================================================
    // 2. ActivityMaster & ActivityReportingConfiguration Domain Tests
    // =========================================================================

    [Fact]
    public void ActivityMaster_CreateAndUpdateDetails_ShouldNormalizeAndValidateCode()
    {
        var activity = ActivityMaster.Create(
            activityCode: "  getdata  ",
            activityName: "  GetData Daily Collection  ",
            description: "  Branch data pull verification  ",
            createdBy: "admin.ops");

        Assert.Equal("GETDATA", activity.ActivityCode);
        Assert.Equal("GetData Daily Collection", activity.ActivityName);
        Assert.Equal("Branch data pull verification", activity.Description);
        Assert.True(activity.IsActive);

        activity.UpdateDetails(
            activityCode: " get_data_v2 ",
            activityName: " GetData v2 ",
            description: null,
            configurationJson: "{\"retry\":3}",
            isActive: false,
            updatedBy: "super.admin");

        Assert.Equal("GET_DATA_V2", activity.ActivityCode);
        Assert.Equal("GetData v2", activity.ActivityName);
        Assert.Null(activity.Description);
        Assert.Equal("{\"retry\":3}", activity.ConfigurationJson);
        Assert.False(activity.IsActive);
    }

    [Fact]
    public void CalculateDailySlotTimes_DefaultRffoSchedule_ShouldProduceExact19SlotsFrom0900To1800()
    {
        var config = ActivityReportingConfiguration.Create(
            activityId: 1,
            reportingStartTime: new TimeOnly(9, 0),
            reportingEndTime: new TimeOnly(18, 0),
            reportingIntervalMinutes: 30,
            timeZoneId: "Asia/Kolkata",
            effectiveFrom: new DateOnly(2026, 1, 1));

        var slots = config.CalculateDailySlotTimes();

        Assert.Equal(19, slots.Count);
        Assert.Equal(new TimeOnly(9, 0), slots[0]);
        Assert.Equal(new TimeOnly(9, 30), slots[1]);
        Assert.Equal(new TimeOnly(17, 30), slots[17]);
        Assert.Equal(new TimeOnly(18, 0), slots[18]);

        var utcInstances = config.CalculateDailySlotsWithUtc(new DateOnly(2026, 11, 7));
        Assert.Equal(19, utcInstances.Count);
        // 09:00 IST = 03:30 UTC; 18:00 IST = 12:30 UTC
        Assert.Equal(new DateTime(2026, 11, 7, 3, 30, 0, DateTimeKind.Utc), utcInstances[0].ScheduledExecutionUtc);
        Assert.Equal(new DateTime(2026, 11, 7, 12, 30, 0, DateTimeKind.Utc), utcInstances[18].ScheduledExecutionUtc);
        Assert.All(utcInstances, s =>
        {
            Assert.False(s.WasAdjustedForDstGap);
            Assert.False(s.WasAmbiguousDstTime);
        });
    }

    [Fact]
    public void CalculateDailySlotTimes_WithNonAlignedInterval_ShouldStopAtLastSlotWithinWindow()
    {
        // 09:00 to 10:00 with 25-minute interval -> 09:00, 09:25, 09:50 (10:15 exceeds 10:00)
        var config = ActivityReportingConfiguration.Create(
            activityId: 1,
            reportingStartTime: new TimeOnly(9, 0),
            reportingEndTime: new TimeOnly(10, 0),
            reportingIntervalMinutes: 25,
            timeZoneId: "Asia/Kolkata",
            effectiveFrom: new DateOnly(2026, 1, 1));

        var slots = config.CalculateDailySlotTimes();

        Assert.Equal(
            new[] { new TimeOnly(9, 0), new TimeOnly(9, 25), new TimeOnly(9, 50) },
            slots);
    }

    [Theory]
    [InlineData(18, 0, 9, 0)]  // end before start
    [InlineData(9, 0, 9, 0)]   // end equal to start
    public void ValidateSchedule_WhenEndTimeNotAfterStartTime_ShouldThrow(
        int startHour,
        int startMinute,
        int endHour,
        int endMinute)
    {
        Assert.Throws<ArgumentException>(() =>
            ActivityReportingConfiguration.ValidateSchedule(
                reportingStartTime: new TimeOnly(startHour, startMinute),
                reportingEndTime: new TimeOnly(endHour, endMinute),
                reportingIntervalMinutes: 30,
                timeZoneId: "Asia/Kolkata",
                effectiveFrom: new DateOnly(2026, 1, 1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(1441)]
    public void ValidateSchedule_WithInvalidInterval_ShouldThrow(int invalidInterval)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ActivityReportingConfiguration.ValidateSchedule(
                reportingStartTime: new TimeOnly(9, 0),
                reportingEndTime: new TimeOnly(18, 0),
                reportingIntervalMinutes: invalidInterval,
                timeZoneId: "Asia/Kolkata",
                effectiveFrom: new DateOnly(2026, 1, 1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Invalid/NoSuchTimeZone")]
    public void ValidateSchedule_WithInvalidTimeZone_ShouldThrow(string invalidTimeZone)
    {
        Assert.Throws<ArgumentException>(() =>
            ActivityReportingConfiguration.ValidateSchedule(
                reportingStartTime: new TimeOnly(9, 0),
                reportingEndTime: new TimeOnly(18, 0),
                reportingIntervalMinutes: 30,
                timeZoneId: invalidTimeZone,
                effectiveFrom: new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void EffectivePeriods_CloseAndOverlapValidation_ShouldRejectOverlappingAndAllowSequentialRanges()
    {
        var firstPeriod = ActivityReportingConfiguration.Create(
            activityId: 1,
            reportingStartTime: new TimeOnly(9, 0),
            reportingEndTime: new TimeOnly(18, 0),
            reportingIntervalMinutes: 30,
            timeZoneId: "Asia/Kolkata",
            effectiveFrom: new DateOnly(2026, 1, 1));
        firstPeriod.ConfigurationId = 1;

        // EffectiveTo earlier than EffectiveFrom must throw
        Assert.Throws<InvalidOperationException>(() =>
            firstPeriod.CloseEffectivePeriod(new DateOnly(2025, 12, 31)));

        // Creating a second period while firstPeriod is still open-ended (EffectiveTo == null) must throw overlap error
        Assert.Throws<InvalidOperationException>(() =>
            ActivityReportingConfiguration.Create(
                activityId: 1,
                reportingStartTime: new TimeOnly(10, 0),
                reportingEndTime: new TimeOnly(17, 0),
                reportingIntervalMinutes: 60,
                timeZoneId: "Asia/Kolkata",
                effectiveFrom: new DateOnly(2026, 6, 1),
                existingConfigurations: new[] { firstPeriod }));

        // Close firstPeriod on 2026-05-31
        firstPeriod.CloseEffectivePeriod(new DateOnly(2026, 5, 31), "admin.ops");
        Assert.Equal(new DateOnly(2026, 5, 31), firstPeriod.EffectiveTo);

        // Creating a period starting 2026-05-31 still overlaps on boundary date 2026-05-31
        Assert.Throws<InvalidOperationException>(() =>
            ActivityReportingConfiguration.Create(
                activityId: 1,
                reportingStartTime: new TimeOnly(10, 0),
                reportingEndTime: new TimeOnly(17, 0),
                reportingIntervalMinutes: 60,
                timeZoneId: "Asia/Kolkata",
                effectiveFrom: new DateOnly(2026, 5, 31),
                existingConfigurations: new[] { firstPeriod }));

        // Creating a sequential period starting 2026-06-01 succeeds cleanly
        var secondPeriod = ActivityReportingConfiguration.Create(
            activityId: 1,
            reportingStartTime: new TimeOnly(10, 0),
            reportingEndTime: new TimeOnly(17, 0),
            reportingIntervalMinutes: 60,
            timeZoneId: "Asia/Kolkata",
            effectiveFrom: new DateOnly(2026, 6, 1),
            existingConfigurations: new[] { firstPeriod });

        Assert.Equal(new DateOnly(2026, 6, 1), secondPeriod.EffectiveFrom);
        Assert.Null(secondPeriod.EffectiveTo);
    }

    [Fact]
    public void ConvertSlotTimeToUtc_DaylightSavingSpringForwardAndFallBack_ShouldBehaveDeterministically()
    {
        var easternTz = ActivityReportingConfiguration.ResolveValidatedTimeZone("America/New_York");

        // 1. Spring-Forward DST gap in America/New_York on 2026-03-08:
        // Clocks jump from 02:00 EST (UTC-5) to 03:00 EDT (UTC-4), so 02:30 local time is invalid.
        // Shifting forward by the 1-hour gap maps 02:30 -> 03:30 EDT = 07:30 UTC.
        var springForwardUtc = ActivityReportingConfiguration.ConvertSlotTimeToUtc(
            reportingDate: new DateOnly(2026, 3, 8),
            slotTime: new TimeOnly(2, 30),
            timeZone: easternTz,
            out var springWasGap,
            out var springWasAmbiguous);

        Assert.True(springWasGap);
        Assert.False(springWasAmbiguous);
        Assert.Equal(new DateTime(2026, 3, 8, 7, 30, 0, DateTimeKind.Utc), springForwardUtc);

        // 2. Fall-Back DST overlap in America/New_York on 2026-11-01:
        // 01:30 occurs twice: first at 01:30 EDT (UTC-4 = 05:30 UTC), then at 01:30 EST (UTC-5 = 06:30 UTC).
        // ConvertSlotTimeToUtc deterministically selects the first chronological occurrence (05:30 UTC).
        var fallBackUtc = ActivityReportingConfiguration.ConvertSlotTimeToUtc(
            reportingDate: new DateOnly(2026, 11, 1),
            slotTime: new TimeOnly(1, 30),
            timeZone: easternTz,
            out var fallWasGap,
            out var fallWasAmbiguous);

        Assert.False(fallWasGap);
        Assert.True(fallWasAmbiguous);
        Assert.Equal(new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc), fallBackUtc);
    }

    // =========================================================================
    // 3. Reopening Permission Revocation & Expiration Tests
    // =========================================================================

    [Fact]
    public void RevokeReopenPermission_WhenAvailable_ShouldMarkRevokedAndPreventConsumption()
    {
        var reportingDate = new DateOnly(2026, 11, 7);
        var permission = StatusReopenPermission.Grant(
            branchId: 101,
            activityId: 1,
            reportingDate: reportingDate,
            enabledBy: "admin.one",
            reason: "Initial reopen request",
            enabledAtUtc: new DateTime(2026, 11, 7, 5, 0, 0, DateTimeKind.Utc));

        var revokedAtUtc = new DateTime(2026, 11, 7, 5, 10, 0, DateTimeKind.Utc);
        permission.Revoke("admin.two", "Granted for wrong branch in error", revokedAtUtc);

        Assert.Equal(ReopenPermissionStatus.Revoked, permission.PermissionStatus);
        Assert.Equal("admin.two", permission.RevokedBy);
        Assert.Equal("Granted for wrong branch in error", permission.RevocationReason);
        Assert.Equal(revokedAtUtc, permission.RevokedAtUtc);

        // Revoked permission cannot be consumed
        bool consumed = permission.TryConsume(
            expectedBranchId: 101,
            expectedActivityId: 1,
            currentReportingDate: reportingDate,
            telegramUserId: 90001,
            telegramMessageId: 5001,
            consumedAtUtc: revokedAtUtc.AddMinutes(5),
            out var failureReason);

        Assert.False(consumed);
        Assert.Contains("Revoked", failureReason, StringComparison.OrdinalIgnoreCase);

        // Revoking an already revoked permission must throw
        Assert.Throws<InvalidOperationException>(() =>
            permission.Revoke("admin.two", "Duplicate revoke", revokedAtUtc.AddMinutes(1)));
    }

    [Fact]
    public void RevokeReopenPermission_WhenAlreadyConsumed_ShouldThrowAndNeverAllowReuse()
    {
        var reportingDate = new DateOnly(2026, 11, 7);
        var permission = StatusReopenPermission.Grant(
            branchId: 101,
            activityId: 1,
            reportingDate: reportingDate,
            enabledBy: "admin.one",
            reason: "Valid reopen",
            enabledAtUtc: new DateTime(2026, 11, 7, 5, 0, 0, DateTimeKind.Utc));

        var consumedAtUtc = new DateTime(2026, 11, 7, 5, 5, 0, DateTimeKind.Utc);
        Assert.True(permission.TryConsume(101, 1, reportingDate, 90001, 5001, consumedAtUtc, out _));
        Assert.Equal(ReopenPermissionStatus.Consumed, permission.PermissionStatus);

        Assert.Throws<InvalidOperationException>(() =>
            permission.Revoke("admin.two", "Attempt to revoke after consumption", consumedAtUtc.AddMinutes(2)));

        Assert.Equal(ReopenPermissionStatus.Consumed, permission.PermissionStatus);
        Assert.Null(permission.RevokedBy);
    }

    [Fact]
    public void ExpireStalePermissions_ShouldExpireOnlyPriorDateAvailablePermissions()
    {
        var yesterday = new DateOnly(2026, 11, 6);
        var today = new DateOnly(2026, 11, 7);

        var staleAvailable = StatusReopenPermission.Grant(101, 1, yesterday, "admin", "Stale available", DateTime.UtcNow);
        var staleConsumed = StatusReopenPermission.Grant(102, 1, yesterday, "admin", "Stale consumed", DateTime.UtcNow);
        staleConsumed.TryConsume(102, 1, yesterday, 90001, 5001, DateTime.UtcNow, out _);

        var todayAvailable = StatusReopenPermission.Grant(103, 1, today, "admin", "Today available", DateTime.UtcNow);

        int expiredCount = StatusReopenPermission.ExpireStalePermissions(
            new[] { staleAvailable, staleConsumed, todayAvailable },
            currentReportingDate: today);

        Assert.Equal(1, expiredCount);
        Assert.Equal(ReopenPermissionStatus.Expired, staleAvailable.PermissionStatus);
        Assert.Equal(ReopenPermissionStatus.Consumed, staleConsumed.PermissionStatus);
        Assert.Equal(ReopenPermissionStatus.Available, todayAvailable.PermissionStatus);

        // Expired permission cannot be revoked
        Assert.Throws<InvalidOperationException>(() =>
            staleAvailable.Revoke("admin", "Cannot revoke expired", DateTime.UtcNow));
    }
}
