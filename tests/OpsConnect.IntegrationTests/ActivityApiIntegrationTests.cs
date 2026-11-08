using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpsConnect.Api.Authentication;
using OpsConnect.Api.Controllers;
using OpsConnect.Api.Models;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Administration;
using OpsConnect.Application.Common;
using OpsConnect.Application.Organisation;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Telegram;
using OpsConnect.Infrastructure.Persistence;
using OpsConnect.Infrastructure.Services;

namespace OpsConnect.IntegrationTests;

public class ActivityApiIntegrationTests
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
        ActivityStatusController StatusController,
        ActivityConfigurationController ConfigController)
        CreateControllers(
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

        var parser = new TelegramActivityMessageParser();
        var rffoHandler = new RffoActivityUpdateHandler(dbContext, clock);
        var processor = new ActivityUpdateProcessor(parser, new IActivityUpdateHandler[] { rffoHandler });
        var statusQueryService = new ActivityStatusQueryService(dbContext, clock);
        var reopenService = new StatusReopenPermissionService(dbContext, clock);
        var configService = new ActivityConfigurationService(dbContext, clock);

        var principal = CreatePrincipal(username, role, isAuthenticated);

        var statusController = new ActivityStatusController(processor, statusQueryService, reopenService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };

        var configController = new ActivityConfigurationController(configService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };

        return (orgService, statusController, configController);
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
            ZoneCode: "WEST",
            ZoneName: "West Zone",
            StateCode: "MH",
            StateName: "Maharashtra",
            RegionCode: "REG-W",
            RegionName: "Region West",
            BranchCode: "36:03",
            BranchName: "Mumbai Central",
            IsZoneActive: true,
            IsStateActive: true,
            IsRegionActive: true,
            IsBranchActive: true),
        new(
            RowNumber: 5,
            ZoneCode: "EAST",
            ZoneName: "East Zone",
            StateCode: "OD",
            StateName: "Odisha",
            RegionCode: "REG-B",
            RegionName: "Region B",
            BranchCode: "36:04",
            BranchName: "Rourkela Inactive",
            IsZoneActive: true,
            IsStateActive: true,
            IsRegionActive: true,
            IsBranchActive: false)
    ];

    [Fact]
    public async Task AuthorizationPolicies_AndControllerRoleChecks_ShouldEnforceReadSubmitAndManageRules()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:AdminApiKey"] = "Test-Secret-Key-456"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(AuthorizationPolicies.ConfigurePolicies);
        await using var provider = services.BuildServiceProvider();
        var authzService = provider.GetRequiredService<IAuthorizationService>();

        var optionsMonitor = new StaticOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
        var scheme = new AuthenticationScheme(
            OpsConnectAuthenticationHandler.SchemeName,
            OpsConnectAuthenticationHandler.SchemeName,
            typeof(OpsConnectAuthenticationHandler));

        // 1. Viewer principal -> allowed on ActivityRead, forbidden on ActivitySubmit and ActivityManage
        var viewerHttp = new DefaultHttpContext();
        viewerHttp.Request.Headers[OpsConnectAuthenticationHandler.ApiKeyHeaderName] = "Test-Secret-Key-456";
        viewerHttp.Request.Headers[OpsConnectAuthenticationHandler.UserHeaderName] = "viewer.ops";
        viewerHttp.Request.Headers[OpsConnectAuthenticationHandler.RoleHeaderName] = SystemRoles.Viewer;

        var viewerAuthHandler = new OpsConnectAuthenticationHandler(optionsMonitor, NullLoggerFactory.Instance, UrlEncoder.Default, config);
        await viewerAuthHandler.InitializeAsync(scheme, viewerHttp);
        var viewerAuth = await viewerAuthHandler.AuthenticateAsync();
        Assert.True(viewerAuth.Succeeded);

        Assert.True((await authzService.AuthorizeAsync(viewerAuth.Principal!, null, AuthorizationPolicies.ActivityRead)).Succeeded);
        Assert.False((await authzService.AuthorizeAsync(viewerAuth.Principal!, null, AuthorizationPolicies.ActivitySubmit)).Succeeded);
        Assert.False((await authzService.AuthorizeAsync(viewerAuth.Principal!, null, AuthorizationPolicies.ActivityManage)).Succeeded);

        // 2. Admin principal -> allowed on ActivityRead, ActivitySubmit, and ActivityManage
        var adminHttp = new DefaultHttpContext();
        adminHttp.Request.Headers[OpsConnectAuthenticationHandler.ApiKeyHeaderName] = "Test-Secret-Key-456";
        adminHttp.Request.Headers[OpsConnectAuthenticationHandler.UserHeaderName] = "admin.ops";
        adminHttp.Request.Headers[OpsConnectAuthenticationHandler.RoleHeaderName] = SystemRoles.Admin;

        var adminAuthHandler = new OpsConnectAuthenticationHandler(optionsMonitor, NullLoggerFactory.Instance, UrlEncoder.Default, config);
        await adminAuthHandler.InitializeAsync(scheme, adminHttp);
        var adminAuth = await adminAuthHandler.AuthenticateAsync();
        Assert.True(adminAuth.Succeeded);

        Assert.True((await authzService.AuthorizeAsync(adminAuth.Principal!, null, AuthorizationPolicies.ActivityRead)).Succeeded);
        Assert.True((await authzService.AuthorizeAsync(adminAuth.Principal!, null, AuthorizationPolicies.ActivitySubmit)).Succeeded);
        Assert.True((await authzService.AuthorizeAsync(adminAuth.Principal!, null, AuthorizationPolicies.ActivityManage)).Succeeded);

        // 3. Direct controller invocation with unauthenticated user -> 401 Unauthorized
        await using var context = CreateContext();
        var (_, unauthStatusCtrl, unauthConfigCtrl) = CreateControllers(context, username: null, role: null, isAuthenticated: false);

        var unauthRead = await unauthStatusCtrl.GetDailyStatus();
        Assert.IsType<UnauthorizedObjectResult>(unauthRead.Result);

        var unauthSubmit = await unauthStatusCtrl.SubmitActivityUpdate(
            new SubmitActivityUpdateApiRequest("Activity : RFFO\nBranch Code : 36:01\nStatus : Pending\nReason : CashShortage"));
        Assert.IsType<UnauthorizedObjectResult>(unauthSubmit.Result);

        var unauthConfig = await unauthConfigCtrl.CreateOrUpdateActivity(
            new UpsertActivityApiRequest(ActivityName: "Test", ActivityCode: "TEST"));
        Assert.IsType<UnauthorizedObjectResult>(unauthConfig.Result);

        // 4. Direct controller invocation with Viewer role on write endpoints -> 403 Forbidden (ForbidResult)
        var (_, viewerStatusCtrl, viewerConfigCtrl) = CreateControllers(context, username: "viewer.user", role: SystemRoles.Viewer);

        var forbidSubmit = await viewerStatusCtrl.SubmitActivityUpdate(
            new SubmitActivityUpdateApiRequest("Activity : RFFO\nBranch Code : 36:01\nStatus : Pending\nReason : CashShortage"));
        Assert.IsType<ForbidResult>(forbidSubmit.Result);

        var forbidGrant = await viewerStatusCtrl.GrantReopenPermission(
            new GrantReopenPermissionApiRequest("RFFO", "36:01", "Attempted by viewer"));
        Assert.IsType<ForbidResult>(forbidGrant.Result);

        var forbidRevoke = await viewerStatusCtrl.RevokeReopenPermission(
            1,
            new RevokeReopenPermissionApiRequest("Attempted by viewer"));
        Assert.IsType<ForbidResult>(forbidRevoke.Result);

        var forbidConfig = await viewerConfigCtrl.CreateOrUpdateActivity(
            new UpsertActivityApiRequest(ActivityName: "Test", ActivityCode: "TEST"));
        Assert.IsType<ForbidResult>(forbidConfig.Result);
    }

    [Fact]
    public async Task ActivitySubmission_DailyStatus_Filtering_PopulationSummary_AndReopenPermissions_ShouldWorkEndToEnd()
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

            var (orgService, adminStatusCtrl, _) = CreateControllers(
                context,
                username: "chief.admin",
                role: SystemRoles.Admin);

            var (_, viewerStatusCtrl, _) = CreateControllers(
                context,
                username: "branch.viewer",
                role: SystemRoles.Viewer);

            // Seed 3 active branches (36:01, 36:02 in EAST/OD/REG-A; 36:03 in WEST/MH/REG-W) and 1 inactive branch (36:04)
            await orgService.ImportHierarchyAsync(
                CreateTestHierarchyBatch(),
                effectiveFromDate: new DateOnly(2026, 11, 1),
                validateOnly: false,
                importedBy: "chief.admin");

            // 1. Initial Daily Status on Day 1: all 3 active mapped branches are Derived NotReported
            var initialSnapshotResp = await viewerStatusCtrl.GetDailyStatusByActivity("RFFO", reportingDate: Day1);
            var initialSnapshot = Assert.IsType<ActivityDailyStatusSnapshotDto>(
                Assert.IsType<OkObjectResult>(initialSnapshotResp.Result).Value);

            Assert.Equal(3, initialSnapshot.TotalActiveBranches);
            Assert.Equal(0, initialSnapshot.CompletedBranches);
            Assert.Equal(0, initialSnapshot.PendingBranches);
            Assert.Equal(3, initialSnapshot.NotReportedBranches);
            Assert.Equal(0m, initialSnapshot.CompletionPercentage);
            Assert.All(initialSnapshot.Branches, b =>
            {
                Assert.Equal(BranchActivityStatus.NotReported, b.CurrentStatus);
                Assert.True(b.IsDerivedNotReported);
            });

            // 2. Submit RFFO Pending for 36:01 (200 OK)
            var submitPending3601 = await adminStatusCtrl.SubmitActivityUpdate(new SubmitActivityUpdateApiRequest(
                RawMessageText: "Activity : RFFO\nBranch Code : 36:01\nStatus : Pending\nReason : CashShortage",
                ActivityCode: "RFFO",
                TelegramMessageLogId: 50001,
                TelegramChatId: -100999,
                TelegramMessageId: 101,
                TelegramUserId: 8001,
                SenderUsername: "op_3601",
                SenderDisplayName: "Operator 36:01",
                ReportingDate: Day1));

            var pendingResult = Assert.IsType<ActivityUpdateProcessingResult>(
                Assert.IsType<OkObjectResult>(submitPending3601.Result).Value);
            Assert.True(pendingResult.IsAccepted);
            Assert.Equal(TransitionOutcome.Accepted, pendingResult.Outcome);
            Assert.Equal(BranchActivityStatus.NotReported, pendingResult.PreviousStatus);
            Assert.Equal(BranchActivityStatus.Pending, pendingResult.CurrentStatus);

            // 3. Submit RFFO Completed for 36:02 via activity-scoped route (200 OK)
            var submitCompleted3602 = await adminStatusCtrl.SubmitActivityUpdateForActivity(
                "RFFO",
                new SubmitActivityUpdateApiRequest(
                    RawMessageText: "Activity : RFFO\nBranch Code : 36:02\nStatus : Completed",
                    TelegramMessageLogId: 50002,
                    TelegramUserId: 8002,
                    SenderDisplayName: "Operator 36:02",
                    ReportingDate: Day1));

            var completed3602Result = Assert.IsType<ActivityUpdateProcessingResult>(
                Assert.IsType<OkObjectResult>(submitCompleted3602.Result).Value);
            Assert.True(completed3602Result.IsAccepted);
            Assert.Equal(BranchActivityStatus.Completed, completed3602Result.CurrentStatus);

            // 4. Duplicate submission with same TelegramMessageLogId = 50002 -> 409 Conflict (DuplicateIgnored)
            var duplicateSubmission = await adminStatusCtrl.SubmitActivityUpdate(new SubmitActivityUpdateApiRequest(
                RawMessageText: "Activity : RFFO\nBranch Code : 36:02\nStatus : Pending\nReason : CashShortage",
                TelegramMessageLogId: 50002,
                ReportingDate: Day1));

            var duplicateConflict = Assert.IsType<ConflictObjectResult>(duplicateSubmission.Result);
            var duplicatePayload = Assert.IsType<ActivityUpdateProcessingResult>(duplicateConflict.Value);
            Assert.False(duplicatePayload.IsAccepted);
            Assert.Equal(MessageProcessingStatus.DuplicateIgnored, duplicatePayload.ProcessingStatus);

            // 5. Invalid & unsupported activity submissions -> 400 Bad Request
            var missingBodyResp = await adminStatusCtrl.SubmitActivityUpdate(new SubmitActivityUpdateApiRequest("   "));
            Assert.IsType<BadRequestObjectResult>(missingBodyResp.Result);

            var mismatchedActivityResp = await adminStatusCtrl.SubmitActivityUpdateForActivity(
                "RFFO",
                new SubmitActivityUpdateApiRequest(
                    RawMessageText: "Activity : GETDATA\nBranch Code : 36:01\nStatus : Pending\nReason : CashShortage"));
            var mismatchBadReq = Assert.IsType<BadRequestObjectResult>(mismatchedActivityResp.Result);
            var mismatchPayload = Assert.IsType<ActivityUpdateProcessingResult>(mismatchBadReq.Value);
            Assert.Equal(MessageProcessingStatus.RejectedInvalidFormat, mismatchPayload.ProcessingStatus);

            var unsupportedGetDataResp = await adminStatusCtrl.SubmitActivityUpdate(new SubmitActivityUpdateApiRequest(
                RawMessageText: "Activity : GETDATA\nBranch Code : 36:01\nStatus : Pending\nReason : CashShortage",
                ActivityCode: "GETDATA",
                ReportingDate: Day1));
            var unsupportedBadReq = Assert.IsType<BadRequestObjectResult>(unsupportedGetDataResp.Result);
            var unsupportedPayload = Assert.IsType<ActivityUpdateProcessingResult>(unsupportedBadReq.Value);
            Assert.Equal(MessageProcessingStatus.RejectedUnknownActivity, unsupportedPayload.ProcessingStatus);

            // 6. Verify Filtering + Full Population Summary Counts!
            // Population has 3 active branches: 36:01 (Pending), 36:02 (Completed), 36:03 (NotReported).
            // Filter by Zone=WEST (returns only 36:03 in Branches, but Summary MUST still show Total=3, Completed=1, Pending=1, NotReported=1, Pct=33.33!)
            var filteredSnapshotResp = await viewerStatusCtrl.GetDailyStatus(
                activityCode: "RFFO",
                reportingDate: Day1,
                zoneCode: "WEST",
                status: BranchActivityStatus.NotReported);

            var filteredSnapshot = Assert.IsType<ActivityDailyStatusSnapshotDto>(
                Assert.IsType<OkObjectResult>(filteredSnapshotResp.Result).Value);

            Assert.Equal(3, filteredSnapshot.TotalActiveBranches);
            Assert.Equal(1, filteredSnapshot.CompletedBranches);
            Assert.Equal(1, filteredSnapshot.PendingBranches);
            Assert.Equal(1, filteredSnapshot.NotReportedBranches);
            Assert.Equal(33.33m, filteredSnapshot.CompletionPercentage);

            var singleDisplayedBranch = Assert.Single(filteredSnapshot.Branches);
            Assert.Equal("36:03", singleDisplayedBranch.BranchCode);
            Assert.Equal(BranchActivityStatus.NotReported, singleDisplayedBranch.CurrentStatus);
            Assert.True(singleDisplayedBranch.IsDerivedNotReported);

            // 7. Branch-level current status endpoint
            var branch3602StatusResp = await viewerStatusCtrl.GetBranchCurrentStatus("RFFO", "36:02", reportingDate: Day1);
            var branch3602Status = Assert.IsType<BranchActivityStatusItemDto>(
                Assert.IsType<OkObjectResult>(branch3602StatusResp.Result).Value);
            Assert.Equal(BranchActivityStatus.Completed, branch3602Status.CurrentStatus);
            Assert.False(branch3602Status.IsDerivedNotReported);

            // 8. Reject Completed -> Pending on 36:02 without reopen permission (400 Bad Request)
            var unauthorizedRevertResp = await adminStatusCtrl.SubmitActivityUpdate(new SubmitActivityUpdateApiRequest(
                RawMessageText: "Activity : RFFO\nBranch Code : 36:02\nStatus : Pending\nReason : CashShortage",
                TelegramMessageLogId: 50003,
                ReportingDate: Day1));
            var revertBadReq = Assert.IsType<BadRequestObjectResult>(unauthorizedRevertResp.Result);
            var revertPayload = Assert.IsType<ActivityUpdateProcessingResult>(revertBadReq.Value);
            Assert.Equal(MessageProcessingStatus.RejectedInvalidTransition, revertPayload.ProcessingStatus);

            // 9. Grant Reopen Permission for 36:02 (verifying authenticated identity "chief.admin" is recorded)
            var grantResp = await adminStatusCtrl.GrantReopenPermission(new GrantReopenPermissionApiRequest(
                ActivityCode: "RFFO",
                BranchCode: "36:02",
                Reason: "Authorised correction of premature completion",
                ReportingDate: Day1));

            var grantResult = Assert.IsType<StatusReopenPermissionOperationResult>(
                Assert.IsType<OkObjectResult>(grantResp.Result).Value);
            Assert.True(grantResult.IsSuccess);
            Assert.NotNull(grantResult.Permission);
            Assert.Equal("chief.admin", grantResult.Permission!.EnabledBy);
            Assert.Equal(ReopenPermissionStatus.Available, grantResult.Permission.PermissionStatus);

            // Duplicate Available grant -> 409 Conflict
            var duplicateGrantResp = await adminStatusCtrl.GrantReopenPermission(new GrantReopenPermissionApiRequest(
                ActivityCode: "RFFO",
                BranchCode: "36:02",
                Reason: "Second duplicate grant attempt",
                ReportingDate: Day1));
            Assert.IsType<ConflictObjectResult>(duplicateGrantResp.Result);

            // 10. Consume permission via Completed -> Pending submission on 36:02 (200 OK, Reopened)
            var reopenSubmitResp = await adminStatusCtrl.SubmitActivityUpdate(new SubmitActivityUpdateApiRequest(
                RawMessageText: "Activity : RFFO\nBranch Code : 36:02\nStatus : Pending\nReason : CashShortage",
                TelegramMessageLogId: 50004,
                TelegramUserId: 8002,
                ReportingDate: Day1));

            var reopenPayload = Assert.IsType<ActivityUpdateProcessingResult>(
                Assert.IsType<OkObjectResult>(reopenSubmitResp.Result).Value);
            Assert.True(reopenPayload.IsAccepted);
            Assert.Equal(TransitionOutcome.AcceptedViaReopen, reopenPayload.Outcome);
            Assert.True(reopenPayload.DidConsumeReopenPermission);
            Assert.Equal(grantResult.Permission.PermissionId, reopenPayload.ConsumedPermissionId);

            // Attempting to revoke an already-consumed permission -> 409 Conflict
            var revokeConsumedResp = await adminStatusCtrl.RevokeReopenPermission(
                grantResult.Permission.PermissionId,
                new RevokeReopenPermissionApiRequest("Too late to revoke"));
            Assert.IsType<ConflictObjectResult>(revokeConsumedResp.Result);

            // 11. Grant and Revoke a permission on 36:01, and Grant and Expire a stale permission
            var grant3601Resp = await adminStatusCtrl.GrantReopenPermission(new GrantReopenPermissionApiRequest(
                ActivityCode: "RFFO",
                BranchCode: "36:01",
                Reason: "Grant to test revocation",
                ReportingDate: Day1));
            var perm3601 = Assert.IsType<StatusReopenPermissionOperationResult>(
                Assert.IsType<OkObjectResult>(grant3601Resp.Result).Value).Permission!;

            var revoke3601Resp = await adminStatusCtrl.RevokeReopenPermission(
                perm3601.PermissionId,
                new RevokeReopenPermissionApiRequest("No longer needed"));
            var revoked3601 = Assert.IsType<StatusReopenPermissionOperationResult>(
                Assert.IsType<OkObjectResult>(revoke3601Resp.Result).Value).Permission!;
            Assert.Equal(ReopenPermissionStatus.Revoked, revoked3601.PermissionStatus);
            Assert.Equal("chief.admin", revoked3601.RevokedBy);

            var grant3603Resp = await adminStatusCtrl.GrantReopenPermission(new GrantReopenPermissionApiRequest(
                ActivityCode: "RFFO",
                BranchCode: "36:03",
                Reason: "Grant on Day 1 to expire on Day 2",
                ReportingDate: Day1));
            Assert.IsType<OkObjectResult>(grant3603Resp.Result);

            var expireResp = await adminStatusCtrl.ExpireStalePermissions(
                new ExpireStalePermissionsApiRequest(ActivityCode: "RFFO", AsOfReportingDate: Day2));
            var expirePayload = Assert.IsType<ExpireStalePermissionsApiResponse>(
                Assert.IsType<OkObjectResult>(expireResp.Result).Value);
            Assert.Equal(1, expirePayload.ExpiredCount);
            Assert.Equal("chief.admin", expirePayload.TriggeredBy);

            // 12. View permission history (Consumed, Revoked, Expired all visible and filterable)
            var allPermsResp = await viewerStatusCtrl.GetReopenPermissions(activityCode: "RFFO", reportingDate: Day1);
            var allPerms = Assert.IsAssignableFrom<IReadOnlyList<StatusReopenPermissionDto>>(
                Assert.IsType<OkObjectResult>(allPermsResp.Result).Value);
            Assert.Equal(3, allPerms.Count);
            Assert.Contains(allPerms, p => p.PermissionStatus == ReopenPermissionStatus.Consumed);
            Assert.Contains(allPerms, p => p.PermissionStatus == ReopenPermissionStatus.Revoked);
            Assert.Contains(allPerms, p => p.PermissionStatus == ReopenPermissionStatus.Expired);

            // 13. Branch activity history for 36:02 shows Accepted, Rejected, and Reopened entries
            var history3602Resp = await viewerStatusCtrl.GetBranchActivityHistory("RFFO", "36:02", reportingDate: Day1);
            var history3602 = Assert.IsType<BranchActivityHistoryResultDto>(
                Assert.IsType<OkObjectResult>(history3602Resp.Result).Value);
            Assert.Equal(BranchActivityStatus.Pending, history3602.CurrentStatusOnDate);
            Assert.Equal(1, history3602.ReopenedCountOnDate);
            Assert.Equal(3, history3602.HistoryEntries.Count);

            await tx.RollbackAsync();
        });
    }

    [Fact]
    public async Task ActivityConfigurationEndpoints_ShouldManageActivitiesSettingsSchedulesSlotPreviewsAndMaskSecrets()
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

            var (_, _, adminConfigCtrl) = CreateControllers(
                context,
                username: "config.admin",
                role: SystemRoles.SuperAdmin);

            var (_, _, viewerConfigCtrl) = CreateControllers(
                context,
                username: "config.viewer",
                role: SystemRoles.Viewer);

            // 1. Create a new Activity Master record (GETDATA)
            var createResp = await adminConfigCtrl.CreateOrUpdateActivity(new UpsertActivityApiRequest(
                ActivityCode: "GETDATA",
                ActivityName: "GetData Operational Sync",
                Description: "Daily data synchronization check",
                ConfigurationJson: "{\"version\":1}",
                IsActive: true,
                ChangeReason: "Initial creation via API"));

            var createResult = Assert.IsType<ActivityConfigurationOperationResult>(
                Assert.IsType<OkObjectResult>(createResp.Result).Value);
            Assert.True(createResult.IsSuccess);
            Assert.Equal("GETDATA", createResult.Activity!.ActivityCode);
            Assert.True(createResult.Activity.IsActive);

            // 2. Disable and Re-enable Activity via PATCH /api/activities/{activityCode}/active-state
            var disableResp = await adminConfigCtrl.SetActivityActiveState(
                "GETDATA",
                new SetActivityActiveStateApiRequest(IsActive: false, ChangeReason: "Maintenance window"));
            var disableResult = Assert.IsType<ActivityConfigurationOperationResult>(
                Assert.IsType<OkObjectResult>(disableResp.Result).Value);
            Assert.False(disableResult.Activity!.IsActive);

            var enableResp = await adminConfigCtrl.SetActivityActiveState(
                "GETDATA",
                new SetActivityActiveStateApiRequest(IsActive: true, ChangeReason: "Maintenance complete"));
            var enableResult = Assert.IsType<ActivityConfigurationOperationResult>(
                Assert.IsType<OkObjectResult>(enableResp.Result).Value);
            Assert.True(enableResult.Activity!.IsActive);

            // 3. Set normal and sensitive key-value configuration settings
            var normalSettingResp = await adminConfigCtrl.SetActivitySetting(
                "GETDATA",
                "BATCH_TIMEOUT_SECONDS",
                new SetActivitySettingApiRequest("120", "Batch timeout in seconds", "Configure timeout"));
            Assert.IsType<OkObjectResult>(normalSettingResp.Result);

            var secretSettingResp = await adminConfigCtrl.SetActivitySetting(
                "GETDATA",
                "WEBHOOK_SECRET_TOKEN",
                new SetActivitySettingApiRequest("SuperSecretValue123!", "Sensitive webhook token", "Set secret token"));
            var secretResult = Assert.IsType<ActivityConfigurationOperationResult>(
                Assert.IsType<OkObjectResult>(secretSettingResp.Result).Value);

            // Verify sensitive value is masked in returned ActivityDetailsDto
            var returnedSecretSetting = Assert.Single(
                secretResult.Activity!.Settings,
                s => s.ConfigKey == "WEBHOOK_SECRET_TOKEN");
            Assert.Equal("***MASKED***", returnedSecretSetting.ConfigValue);

            // 4. Create initial effective-dated reporting schedule (from 2026-11-01, open-ended, 60-min interval -> 10 slots)
            var schedule1Resp = await adminConfigCtrl.UpdateReportingSchedule(
                "GETDATA",
                new UpdateReportingScheduleApiRequest(
                    ReportingStartTime: new TimeOnly(9, 0),
                    ReportingEndTime: new TimeOnly(18, 0),
                    ReportingIntervalMinutes: 60,
                    EffectiveFrom: new DateOnly(2026, 11, 1),
                    EffectiveTo: null,
                    TimeZoneId: "Asia/Kolkata",
                    IsReportingEnabled: true,
                    StopWhenAllCompleted: true,
                    ChangeReason: "Initial 60-minute schedule"));

            var schedule1Result = Assert.IsType<ReportingConfigurationUpdateResult>(
                Assert.IsType<OkObjectResult>(schedule1Resp.Result).Value);
            Assert.True(schedule1Result.IsSuccess);
            Assert.Equal(10, schedule1Result.ActiveConfiguration!.CalculatedDailySlotTimes.Count);

            // 5. Update effective-dated schedule starting 2026-11-15 (30-min interval -> 19 slots; auto-closes prior schedule at 2026-11-14)
            var schedule2Resp = await adminConfigCtrl.UpdateReportingSchedule(
                "GETDATA",
                new UpdateReportingScheduleApiRequest(
                    ReportingStartTime: new TimeOnly(9, 0),
                    ReportingEndTime: new TimeOnly(18, 0),
                    ReportingIntervalMinutes: 30,
                    EffectiveFrom: new DateOnly(2026, 11, 15),
                    EffectiveTo: null,
                    TimeZoneId: "Asia/Kolkata",
                    IsReportingEnabled: true,
                    StopWhenAllCompleted: true,
                    ChangeReason: "Switch to 30-minute slots from Nov 15",
                    AutoClosePreviousOpenPeriod: true));

            var schedule2Result = Assert.IsType<ReportingConfigurationUpdateResult>(
                Assert.IsType<OkObjectResult>(schedule2Resp.Result).Value);
            Assert.True(schedule2Result.IsSuccess);
            Assert.Equal(19, schedule2Result.ActiveConfiguration!.CalculatedDailySlotTimes.Count);

            // 6. Reject overlapping effective schedule (2026-11-10 to 2026-11-20 overlaps closed 2026-11-01..2026-11-14) -> 409 Conflict
            var overlapResp = await adminConfigCtrl.UpdateReportingSchedule(
                "GETDATA",
                new UpdateReportingScheduleApiRequest(
                    ReportingStartTime: new TimeOnly(10, 0),
                    ReportingEndTime: new TimeOnly(17, 0),
                    ReportingIntervalMinutes: 30,
                    EffectiveFrom: new DateOnly(2026, 11, 10),
                    EffectiveTo: new DateOnly(2026, 11, 20),
                    AutoClosePreviousOpenPeriod: false));
            Assert.IsType<ConflictObjectResult>(overlapResp.Result);

            // 7. Reject invalid schedule (EndTime <= StartTime) -> 400 Bad Request
            var invalidScheduleResp = await adminConfigCtrl.UpdateReportingSchedule(
                "GETDATA",
                new UpdateReportingScheduleApiRequest(
                    ReportingStartTime: new TimeOnly(18, 0),
                    ReportingEndTime: new TimeOnly(9, 0),
                    ReportingIntervalMinutes: 30,
                    EffectiveFrom: new DateOnly(2026, 12, 1)));
            Assert.IsType<BadRequestObjectResult>(invalidScheduleResp.Result);

            // 8. Retrieve calculated reporting slots for a specific date & preview custom slots
            var slotsNov10Resp = await viewerConfigCtrl.GetReportingSlots("GETDATA", asOfReportingDate: new DateOnly(2026, 11, 10));
            var slotsNov10 = Assert.IsType<ReportingSlotsPreviewResultDto>(
                Assert.IsType<OkObjectResult>(slotsNov10Resp.Result).Value);
            Assert.Equal(10, slotsNov10.TotalSlotsCount);

            var slotsNov20Resp = await viewerConfigCtrl.GetReportingSlots("GETDATA", asOfReportingDate: new DateOnly(2026, 11, 20));
            var slotsNov20 = Assert.IsType<ReportingSlotsPreviewResultDto>(
                Assert.IsType<OkObjectResult>(slotsNov20Resp.Result).Value);
            Assert.Equal(19, slotsNov20.TotalSlotsCount);

            var previewResp = viewerConfigCtrl.PreviewReportingSlots(new PreviewReportingSlotsApiRequest(
                ReportingStartTime: new TimeOnly(9, 0),
                ReportingEndTime: new TimeOnly(11, 0),
                ReportingIntervalMinutes: 30,
                TimeZoneId: "Asia/Kolkata"));
            var previewDto = Assert.IsType<ReportingSlotsPreviewResultDto>(
                Assert.IsType<OkObjectResult>(previewResp.Result).Value);
            Assert.Equal(5, previewDto.TotalSlotsCount);

            // 9. Verify Configuration History records authenticated user "config.admin" and masks secret values
            var historyResp = await viewerConfigCtrl.GetActivityConfigurationHistory("GETDATA");
            var historyEntries = Assert.IsAssignableFrom<IReadOnlyList<ConfigurationHistoryEntryDto>>(
                Assert.IsType<OkObjectResult>(historyResp.Result).Value);

            Assert.True(historyEntries.Count >= 6);
            Assert.All(historyEntries, h => Assert.Equal("config.admin", h.ChangedBy));
            Assert.DoesNotContain(historyEntries, h => (h.NewValueJson ?? string.Empty).Contains("SuperSecretValue123!"));

            await tx.RollbackAsync();
        });
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T currentValue) => CurrentValue = currentValue;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
