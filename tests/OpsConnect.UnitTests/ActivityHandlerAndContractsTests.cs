using Microsoft.Extensions.DependencyInjection;
using OpsConnect.Application;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Activities.RFFO;
using OpsConnect.Application.Administration;
using OpsConnect.Application.Common;
using OpsConnect.Application.Organisation;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Telegram;

namespace OpsConnect.UnitTests;

public class ActivityHandlerAndContractsTests
{
    private sealed class StubRffoHandler : ActivityUpdateHandlerBase, IRffoActivityUpdateHandler
    {
        public StubRffoHandler()
            : base(RffoActivityDescriptor.ActivityCode)
        {
        }

        public ActivityUpdateSubmissionCommand? LastReceivedCommand { get; private set; }

        protected override Task<ActivityUpdateProcessingResult> HandleValidatedUpdateAsync(
            ActivityUpdateSubmissionCommand command,
            CancellationToken cancellationToken)
        {
            LastReceivedCommand = command;
            return Task.FromResult(new ActivityUpdateProcessingResult(
                IsAccepted: true,
                ProcessingStatus: MessageProcessingStatus.Accepted,
                Outcome: TransitionOutcome.Accepted,
                ActivityCode: command.ActivityCode,
                ActivityId: 1,
                BranchCode: command.BranchCode,
                BranchId: 101,
                ReportingDate: command.ExplicitReportingDate ?? new DateOnly(2026, 11, 8),
                PreviousStatus: BranchActivityStatus.NotReported,
                CurrentStatus: command.IncomingStatus,
                DidStatusChange: true,
                DidConsumeReopenPermission: false,
                ConsumedPermissionId: null,
                DidReactivateReportingCycle: false,
                DidTriggerImmediateFinalReportCondition: false,
                RejectionReason: null,
                ReplyMessage: $"RFFO status updated to {command.IncomingStatus} for branch {command.BranchCode}."));
        }
    }

    private sealed class StubGetDataHandler : ActivityUpdateHandlerBase
    {
        public StubGetDataHandler()
            : base(ActivityModuleConstants.GetDataActivityCode)
        {
        }

        public ActivityUpdateSubmissionCommand? LastReceivedCommand { get; private set; }

        protected override Task<ActivityUpdateProcessingResult> HandleValidatedUpdateAsync(
            ActivityUpdateSubmissionCommand command,
            CancellationToken cancellationToken)
        {
            LastReceivedCommand = command;
            return Task.FromResult(new ActivityUpdateProcessingResult(
                IsAccepted: true,
                ProcessingStatus: MessageProcessingStatus.Accepted,
                Outcome: TransitionOutcome.Accepted,
                ActivityCode: command.ActivityCode,
                ActivityId: 2,
                BranchCode: command.BranchCode,
                BranchId: 101,
                ReportingDate: command.ExplicitReportingDate ?? new DateOnly(2026, 11, 8),
                PreviousStatus: BranchActivityStatus.NotReported,
                CurrentStatus: command.IncomingStatus,
                DidStatusChange: true,
                DidConsumeReopenPermission: false,
                ConsumedPermissionId: null,
                DidReactivateReportingCycle: false,
                DidTriggerImmediateFinalReportCondition: false,
                RejectionReason: null,
                ReplyMessage: $"GETDATA status updated to {command.IncomingStatus} for branch {command.BranchCode}."));
        }
    }

    // =========================================================================
    // 1. Pluggable Activity Handler Resolution & Dispatch Tests
    // =========================================================================

    [Fact]
    public async Task ActivityUpdateProcessor_ShouldResolveRffoAndGetDataHandlersIndependently()
    {
        var rffoHandler = new StubRffoHandler();
        var getDataHandler = new StubGetDataHandler();
        var processor = new ActivityUpdateProcessor(
            new TelegramActivityMessageParser(),
            new IActivityUpdateHandler[] { rffoHandler, getDataHandler });

        Assert.Equal(new[] { "GETDATA", "RFFO" }, processor.RegisteredActivityCodes);
        Assert.Same(rffoHandler, processor.ResolveHandler("RFFO"));
        Assert.Same(rffoHandler, processor.ResolveHandler("  rffo  "));
        Assert.Same(getDataHandler, processor.ResolveHandler("getdata"));

        const string rffoRawMessage = """
            Activity- rffo
            BranchID- 36:01
            Status- Pending
            """;

        var rffoResult = await processor.ProcessRawMessageAsync(new RawActivityMessageSubmissionRequest(
            RawMessageText: rffoRawMessage,
            TelegramMessageLogId: 9001,
            TelegramUserId: 50001,
            SenderDisplayName: "Branch Operator"));

        Assert.True(rffoResult.IsAccepted);
        Assert.Equal(MessageProcessingStatus.Accepted, rffoResult.ProcessingStatus);
        Assert.Equal("RFFO", rffoResult.ActivityCode);
        Assert.Equal("36:01", rffoResult.BranchCode);
        Assert.NotNull(rffoHandler.LastReceivedCommand);
        Assert.Null(getDataHandler.LastReceivedCommand);
    }

    [Fact]
    public async Task ActivityUpdateProcessor_WithUnsupportedActivity_ShouldReturnRejectedUnknownActivity()
    {
        var rffoHandler = new StubRffoHandler();
        var processor = new ActivityUpdateProcessor(
            new TelegramActivityMessageParser(),
            new IActivityUpdateHandler[] { rffoHandler });

        Assert.Null(processor.ResolveHandler("UNKNOWN_ACT"));

        const string unsupportedActivityMessage = """
            Activity- UNKNOWN_ACT
            BranchID- 36:01
            Status- Completed
            """;

        var result = await processor.ProcessRawMessageAsync(
            new RawActivityMessageSubmissionRequest(unsupportedActivityMessage));

        Assert.False(result.IsAccepted);
        Assert.Equal(MessageProcessingStatus.RejectedUnknownActivity, result.ProcessingStatus);
        Assert.Equal(TransitionOutcome.Rejected, result.Outcome);
        Assert.Equal("UNKNOWN_ACT", result.ActivityCode);
        Assert.Equal("36:01", result.BranchCode);
        Assert.Contains("UNKNOWN_ACT", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(rffoHandler.LastReceivedCommand);
    }

    [Fact]
    public async Task ActivityUpdateProcessor_WithInvalidRawMessage_ShouldReturnRejectedInvalidFormatWithoutInvokingHandler()
    {
        var rffoHandler = new StubRffoHandler();
        var processor = new ActivityUpdateProcessor(
            new TelegramActivityMessageParser(),
            new IActivityUpdateHandler[] { rffoHandler });

        var result = await processor.ProcessRawMessageAsync(
            new RawActivityMessageSubmissionRequest("Invalid message without fields"));

        Assert.False(result.IsAccepted);
        Assert.Equal(MessageProcessingStatus.RejectedInvalidFormat, result.ProcessingStatus);
        Assert.Equal(TransitionOutcome.Rejected, result.Outcome);
        Assert.NotNull(result.ParseErrors);
        Assert.NotEmpty(result.ParseErrors!);
        Assert.Null(rffoHandler.LastReceivedCommand);
    }

    [Fact]
    public void ActivityUpdateProcessor_WithDuplicateHandlerRegistration_ShouldThrowInvalidOperationException()
    {
        var handler1 = new StubRffoHandler();
        var handler2 = new StubRffoHandler();

        Assert.Throws<InvalidOperationException>(() =>
            new ActivityUpdateProcessor(
                new TelegramActivityMessageParser(),
                new IActivityUpdateHandler[] { handler1, handler2 }));
    }

    [Fact]
    public async Task ActivityUpdateHandlerBase_WhenInvokedWithMismatchedActivityCode_ShouldThrowInvalidOperationException()
    {
        var rffoHandler = new StubRffoHandler();
        var getDataCommand = new ActivityUpdateSubmissionCommand(
            ActivityCode: "GETDATA",
            BranchCode: "36:01",
            IncomingStatus: BranchActivityStatus.Pending);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rffoHandler.ProcessUpdateAsync(getDataCommand));
    }

    // =========================================================================
    // 2. Application Contract & DTO Validation Tests
    // =========================================================================

    [Fact]
    public void ActivityUpdateSubmissionCommand_ValidateAndNormalize_ShouldNormalizeAndRejectInvalidInputs()
    {
        var validCommand = new ActivityUpdateSubmissionCommand(
            ActivityCode: " rffo ",
            BranchCode: " 36:01 ",
            IncomingStatus: BranchActivityStatus.Completed,
            SenderUsername: "  user_01 ",
            SenderDisplayName: "  Branch Manager  ");

        var normalized = validCommand.ValidateAndNormalize();
        Assert.Equal("RFFO", normalized.ActivityCode);
        Assert.Equal("36:01", normalized.BranchCode);
        Assert.Equal("user_01", normalized.SenderUsername);
        Assert.Equal("Branch Manager", normalized.SenderDisplayName);

        // NotReported is never valid as an incoming submission status
        var invalidStatusCommand = validCommand with { IncomingStatus = BranchActivityStatus.NotReported };
        Assert.Throws<ArgumentException>(() => invalidStatusCommand.ValidateAndNormalize());

        // Invalid branch code format
        var invalidBranchCommand = validCommand with { BranchCode = "36 : 01" };
        Assert.Throws<ArgumentException>(() => invalidBranchCommand.ValidateAndNormalize());
    }

    [Fact]
    public void GrantAndRevokeStatusReopenPermissionRequests_ValidateAndNormalize_ShouldEnforceMandatoryFields()
    {
        var grant = new GrantStatusReopenPermissionRequest(
            ActivityCode: " rffo ",
            BranchCode: " 36:01 ",
            ReportingDate: new DateOnly(2026, 11, 8),
            EnabledBy: " admin.ops ",
            Reason: " Branch reported Completed by mistake ").ValidateAndNormalize();

        Assert.Equal("RFFO", grant.ActivityCode);
        Assert.Equal("36:01", grant.BranchCode);
        Assert.Equal("admin.ops", grant.EnabledBy);
        Assert.Equal("Branch reported Completed by mistake", grant.Reason);

        Assert.Throws<ArgumentException>(() =>
            (grant with { EnabledBy = "   " }).ValidateAndNormalize());
        Assert.Throws<ArgumentException>(() =>
            (grant with { Reason = "" }).ValidateAndNormalize());

        var revoke = new RevokeStatusReopenPermissionRequest(
            PermissionId: 42,
            RevokedBy: " super.admin ",
            Reason: " Wrong branch selected ").ValidateAndNormalize();

        Assert.Equal(42, revoke.PermissionId);
        Assert.Equal("super.admin", revoke.RevokedBy);
        Assert.Equal("Wrong branch selected", revoke.Reason);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (revoke with { PermissionId = 0 }).ValidateAndNormalize());
        Assert.Throws<ArgumentException>(() =>
            (revoke with { RevokedBy = "" }).ValidateAndNormalize());
        Assert.Throws<ArgumentException>(() =>
            (revoke with { Reason = " " }).ValidateAndNormalize());
    }

    [Fact]
    public void ActivityConfigurationRequests_ValidateAndNormalize_ShouldEnforceDomainRules()
    {
        var upsertActivity = new UpsertActivityMasterRequest(
            ActivityCode: " rffo ",
            ActivityName: " Request for Field Force Operations ",
            Description: " Daily monitoring ",
            ChangedBy: " admin.ops ",
            ChangeReason: " Initial update ").ValidateAndNormalize();

        Assert.Equal("RFFO", upsertActivity.ActivityCode);
        Assert.Equal("Request for Field Force Operations", upsertActivity.ActivityName);
        Assert.Equal("admin.ops", upsertActivity.ChangedBy);

        var kvRequest = new SetActivityKeyValueConfigRequest(
            ActivityCode: " rffo ",
            ConfigKey: " max_retries ",
            ConfigValue: " 5 ",
            ChangedBy: " admin.ops ").ValidateAndNormalize();

        Assert.Equal("RFFO", kvRequest.ActivityCode);
        Assert.Equal("MAX_RETRIES", kvRequest.ConfigKey);
        Assert.Equal("5", kvRequest.ConfigValue);

        var scheduleRequest = new UpdateActivityReportingConfigurationRequest(
            ActivityCode: " rffo ",
            ReportingStartTime: new TimeOnly(9, 0),
            ReportingEndTime: new TimeOnly(18, 0),
            ReportingIntervalMinutes: 30,
            TimeZoneId: " Asia/Kolkata ",
            IsReportingEnabled: true,
            StopWhenAllCompleted: true,
            EffectiveFrom: new DateOnly(2026, 11, 8),
            ChangedBy: " admin.ops ").ValidateAndNormalize();

        Assert.Equal("RFFO", scheduleRequest.ActivityCode);
        Assert.Equal("Asia/Kolkata", scheduleRequest.TimeZoneId);

        // Invalid schedule end <= start must fail contract validation
        Assert.Throws<ArgumentException>(() =>
            (scheduleRequest with { ReportingEndTime = new TimeOnly(8, 0) }).ValidateAndNormalize());
    }

    // =========================================================================
    // 3. Dependency Injection Registration Tests
    // =========================================================================

    [Fact]
    public void AddApplicationServices_ShouldRegisterParserProcessorAndPluggableHandlerDescriptors()
    {
        var services = new ServiceCollection();
        services.AddApplicationServices();
        services.AddScoped<IActivityUpdateHandler, StubRffoHandler>();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IBusinessClock) &&
            d.ImplementationType == typeof(SystemBusinessClock) &&
            d.Lifetime == ServiceLifetime.Singleton);

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IOrganisationHierarchyImportValidator) &&
            d.ImplementationType == typeof(OrganisationHierarchyImportValidator) &&
            d.Lifetime == ServiceLifetime.Singleton);

        Assert.Contains(services, d =>
            d.ServiceType == typeof(ITelegramActivityMessageParser) &&
            d.ImplementationType == typeof(TelegramActivityMessageParser) &&
            d.Lifetime == ServiceLifetime.Singleton);

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IActivityUpdateProcessor) &&
            d.ImplementationType == typeof(ActivityUpdateProcessor) &&
            d.Lifetime == ServiceLifetime.Scoped);

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IActivityUpdateHandler) &&
            d.ImplementationType == typeof(StubRffoHandler) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }
}
