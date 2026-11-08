using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsConnect.Api.Authentication;
using OpsConnect.Api.Models;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Telegram;

namespace OpsConnect.Api.Controllers;

[ApiController]
[Route("api/activities")]
[Produces("application/json")]
public class ActivityStatusController : ControllerBase
{
    private readonly IActivityUpdateProcessor _activityUpdateProcessor;
    private readonly IActivityStatusQueryService _activityStatusQueryService;
    private readonly IStatusReopenPermissionService _reopenPermissionService;

    public ActivityStatusController(
        IActivityUpdateProcessor activityUpdateProcessor,
        IActivityStatusQueryService activityStatusQueryService,
        IStatusReopenPermissionService reopenPermissionService)
    {
        _activityUpdateProcessor = activityUpdateProcessor;
        _activityStatusQueryService = activityStatusQueryService;
        _reopenPermissionService = reopenPermissionService;
    }

    /// <summary>
    /// Submits a raw operational activity message through the standalone parser and pluggable activity handler pipeline.
    /// </summary>
    [HttpPost("submissions")]
    [Authorize(Policy = AuthorizationPolicies.ActivitySubmit)]
    [ProducesResponseType(typeof(ActivityUpdateProcessingResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ActivityUpdateProcessingResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ActivityUpdateProcessingResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<ActionResult<ActivityUpdateProcessingResult>> SubmitActivityUpdate(
        [FromBody] SubmitActivityUpdateApiRequest? request,
        CancellationToken cancellationToken = default) =>
        ExecuteSubmissionAsync(routeActivityCode: null, request, cancellationToken);

    /// <summary>
    /// Submits a raw operational activity message scoped to a specific activity code.
    /// </summary>
    [HttpPost("{activityCode}/submissions")]
    [Authorize(Policy = AuthorizationPolicies.ActivitySubmit)]
    [ProducesResponseType(typeof(ActivityUpdateProcessingResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ActivityUpdateProcessingResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ActivityUpdateProcessingResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<ActionResult<ActivityUpdateProcessingResult>> SubmitActivityUpdateForActivity(
        [FromRoute] string activityCode,
        [FromBody] SubmitActivityUpdateApiRequest? request,
        CancellationToken cancellationToken = default) =>
        ExecuteSubmissionAsync(routeActivityCode: activityCode, request, cancellationToken);

    /// <summary>
    /// Retrieves daily branch-wise activity status and full-population summary counts with optional filters and pagination.
    /// </summary>
    [HttpGet("status")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(ActivityDailyStatusSnapshotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<ActionResult<ActivityDailyStatusSnapshotDto>> GetDailyStatus(
        [FromQuery] string activityCode = ActivityModuleConstants.RffoActivityCode,
        [FromQuery] DateOnly? reportingDate = null,
        [FromQuery] string? zoneCode = null,
        [FromQuery] string? stateCode = null,
        [FromQuery] string? regionCode = null,
        [FromQuery] string? branchCode = null,
        [FromQuery] BranchActivityStatus? status = null,
        [FromQuery] string? searchTerm = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        CancellationToken cancellationToken = default) =>
        ExecuteGetDailyStatusAsync(
            activityCode,
            reportingDate,
            zoneCode,
            stateCode,
            regionCode,
            branchCode,
            status,
            searchTerm,
            page,
            pageSize,
            cancellationToken);

    /// <summary>
    /// Retrieves daily branch-wise activity status and full-population summary counts for a specific activity code.
    /// </summary>
    [HttpGet("{activityCode}/status")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(ActivityDailyStatusSnapshotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<ActionResult<ActivityDailyStatusSnapshotDto>> GetDailyStatusByActivity(
        [FromRoute] string activityCode,
        [FromQuery] DateOnly? reportingDate = null,
        [FromQuery] string? zoneCode = null,
        [FromQuery] string? stateCode = null,
        [FromQuery] string? regionCode = null,
        [FromQuery] string? branchCode = null,
        [FromQuery] BranchActivityStatus? status = null,
        [FromQuery] string? searchTerm = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        CancellationToken cancellationToken = default) =>
        ExecuteGetDailyStatusAsync(
            activityCode,
            reportingDate,
            zoneCode,
            stateCode,
            regionCode,
            branchCode,
            status,
            searchTerm,
            page,
            pageSize,
            cancellationToken);

    /// <summary>
    /// Retrieves the current status (including derived NotReported status) for a single branch on a reporting date.
    /// </summary>
    [HttpGet("{activityCode}/branches/{branchCode}/status")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(BranchActivityStatusItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BranchActivityStatusItemDto>> GetBranchCurrentStatus(
        [FromRoute] string activityCode,
        [FromRoute] string branchCode,
        [FromQuery] DateOnly? reportingDate = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryEnsureReadAuthorized(out var authError))
        {
            return authError!;
        }

        if (string.IsNullOrWhiteSpace(activityCode) || string.IsNullOrWhiteSpace(branchCode))
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid request parameters.",
                "Both activityCode and branchCode are required."));
        }

        var item = await _activityStatusQueryService.GetBranchCurrentStatusAsync(
            activityCode,
            branchCode,
            reportingDate,
            cancellationToken);

        if (item is null)
        {
            return NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "Branch or activity status not found.",
                $"No configured activity '{activityCode.Trim().ToUpperInvariant()}' or branch '{branchCode.Trim().ToUpperInvariant()}' was found."));
        }

        return Ok(item);
    }

    /// <summary>
    /// Retrieves the chronological activity status transition history for a branch.
    /// </summary>
    [HttpGet("{activityCode}/branches/{branchCode}/history")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(BranchActivityHistoryResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BranchActivityHistoryResultDto>> GetBranchActivityHistory(
        [FromRoute] string activityCode,
        [FromRoute] string branchCode,
        [FromQuery] DateOnly? reportingDate = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryEnsureReadAuthorized(out var authError))
        {
            return authError!;
        }

        if (string.IsNullOrWhiteSpace(activityCode) || string.IsNullOrWhiteSpace(branchCode))
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid request parameters.",
                "Both activityCode and branchCode are required."));
        }

        var history = await _activityStatusQueryService.GetBranchActivityHistoryAsync(
            activityCode,
            branchCode,
            reportingDate,
            cancellationToken);

        if (history is null)
        {
            return NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "Branch or activity history not found.",
                $"No configured activity '{activityCode.Trim().ToUpperInvariant()}' or branch '{branchCode.Trim().ToUpperInvariant()}' was found."));
        }

        return Ok(history);
    }

    /// <summary>
    /// Retrieves administrator reopening permission records and lifecycle history (Available, Consumed, Expired, Revoked).
    /// </summary>
    [HttpGet("reopen-permissions")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(IReadOnlyList<StatusReopenPermissionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<StatusReopenPermissionDto>>> GetReopenPermissions(
        [FromQuery] string? activityCode = null,
        [FromQuery] string? branchCode = null,
        [FromQuery] DateOnly? reportingDate = null,
        [FromQuery] ReopenPermissionStatus? status = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryEnsureReadAuthorized(out var authError))
        {
            return authError!;
        }

        if (page.HasValue && page.Value < 1)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid pagination parameter.",
                "Page must be at least 1."));
        }

        if (pageSize.HasValue && (pageSize.Value < 1 || pageSize.Value > 500))
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid pagination parameter.",
                "PageSize must be between 1 and 500."));
        }

        try
        {
            var filter = new StatusReopenPermissionFilterQuery(
                ActivityCode: activityCode,
                BranchCode: branchCode,
                ReportingDate: reportingDate,
                Status: status);

            var permissions = await _reopenPermissionService.GetPermissionsHistoryAsync(filter, cancellationToken);

            if (page.HasValue || pageSize.HasValue)
            {
                int currentPage = page ?? 1;
                int size = pageSize ?? 100;
                permissions = permissions
                    .Skip((currentPage - 1) * size)
                    .Take(size)
                    .ToList();
            }

            return Ok(permissions);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid filter parameters.",
                ex.Message));
        }
    }

    /// <summary>
    /// Grants a single-use, same-day administrator reopening permission for a branch and activity.
    /// Uses the authenticated administrator identity for audit logging.
    /// </summary>
    [HttpPost("reopen-permissions")]
    [Authorize(Policy = AuthorizationPolicies.ActivityManage)]
    [ProducesResponseType(typeof(StatusReopenPermissionOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(StatusReopenPermissionOperationResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(StatusReopenPermissionOperationResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(StatusReopenPermissionOperationResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<StatusReopenPermissionOperationResult>> GrantReopenPermission(
        [FromBody] GrantReopenPermissionApiRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!TryEnsureAdminAuthorized(out var adminUser, out var authError))
        {
            return authError!;
        }

        if (request is null)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Missing request payload.",
                "A grant reopening permission request payload is required."));
        }

        try
        {
            var serviceRequest = new GrantStatusReopenPermissionRequest(
                ActivityCode: request.ActivityCode,
                BranchCode: request.BranchCode,
                ReportingDate: request.ReportingDate,
                EnabledBy: adminUser,
                Reason: request.Reason);

            var result = await _reopenPermissionService.GrantPermissionAsync(serviceRequest, cancellationToken);
            if (!result.IsSuccess)
            {
                return result.ErrorCode switch
                {
                    "ActivityNotFound" or "BranchNotFound" => NotFound(result),
                    "DuplicateAvailablePermission" => Conflict(result),
                    _ => BadRequest(result)
                };
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid grant permission request.",
                ex.Message));
        }
    }

    /// <summary>
    /// Revokes an Available reopening permission before it is consumed.
    /// Uses the authenticated administrator identity for audit logging.
    /// </summary>
    [HttpPost("reopen-permissions/{permissionId:long}/revoke")]
    [Authorize(Policy = AuthorizationPolicies.ActivityManage)]
    [ProducesResponseType(typeof(StatusReopenPermissionOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(StatusReopenPermissionOperationResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(StatusReopenPermissionOperationResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(StatusReopenPermissionOperationResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<StatusReopenPermissionOperationResult>> RevokeReopenPermission(
        [FromRoute] long permissionId,
        [FromBody] RevokeReopenPermissionApiRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!TryEnsureAdminAuthorized(out var adminUser, out var authError))
        {
            return authError!;
        }

        if (request is null)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Missing request payload.",
                "A revocation reason payload is required."));
        }

        try
        {
            var serviceRequest = new RevokeStatusReopenPermissionRequest(
                PermissionId: permissionId,
                RevokedBy: adminUser,
                Reason: request.Reason);

            var result = await _reopenPermissionService.RevokePermissionAsync(serviceRequest, cancellationToken);
            if (!result.IsSuccess)
            {
                return result.ErrorCode switch
                {
                    "PermissionNotFound" => NotFound(result),
                    "InvalidPermissionState" => Conflict(result),
                    _ => BadRequest(result)
                };
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid revoke permission request.",
                ex.Message));
        }
    }

    /// <summary>
    /// Expires stale Available reopening permissions from reporting dates earlier than the target reporting date.
    /// </summary>
    [HttpPost("reopen-permissions/expire-stale")]
    [Authorize(Policy = AuthorizationPolicies.ActivityManage)]
    [ProducesResponseType(typeof(ExpireStalePermissionsApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ExpireStalePermissionsApiResponse>> ExpireStalePermissions(
        [FromBody] ExpireStalePermissionsApiRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryEnsureAdminAuthorized(out var adminUser, out var authError))
        {
            return authError!;
        }

        try
        {
            int expiredCount = await _reopenPermissionService.ExpireStalePermissionsAsync(
                request?.ActivityCode,
                request?.AsOfReportingDate,
                cancellationToken);

            return Ok(new ExpireStalePermissionsApiResponse(
                ExpiredCount: expiredCount,
                ActivityCode: request?.ActivityCode?.Trim().ToUpperInvariant(),
                AsOfReportingDate: request?.AsOfReportingDate,
                TriggeredBy: adminUser));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid expire stale permissions request.",
                ex.Message));
        }
    }

    private async Task<ActionResult<ActivityUpdateProcessingResult>> ExecuteSubmissionAsync(
        string? routeActivityCode,
        SubmitActivityUpdateApiRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryEnsureAdminAuthorized(out var authenticatedUser, out var authError))
        {
            return authError!;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.RawMessageText))
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Missing raw activity message.",
                "RawMessageText is required to submit an activity update."));
        }

        if (!string.IsNullOrWhiteSpace(routeActivityCode) &&
            !string.IsNullOrWhiteSpace(request.ActivityCode) &&
            !string.Equals(routeActivityCode.Trim(), request.ActivityCode.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Conflicting activity code.",
                $"Route activityCode '{routeActivityCode.Trim()}' does not match payload activityCode '{request.ActivityCode.Trim()}'."));
        }

        var expectedActivityCode = !string.IsNullOrWhiteSpace(routeActivityCode)
            ? routeActivityCode
            : request.ActivityCode;

        var submissionRequest = new RawActivityMessageSubmissionRequest(
            RawMessageText: request.RawMessageText,
            TelegramMessageLogId: request.TelegramMessageLogId,
            TelegramChatId: request.TelegramChatId,
            TelegramMessageId: request.TelegramMessageId,
            TelegramUserId: request.TelegramUserId,
            SenderUsername: string.IsNullOrWhiteSpace(request.SenderUsername) ? authenticatedUser : request.SenderUsername,
            SenderDisplayName: string.IsNullOrWhiteSpace(request.SenderDisplayName) ? authenticatedUser : request.SenderDisplayName,
            OccurredAtUtc: request.OccurredAtUtc,
            ExplicitReportingDate: request.ReportingDate,
            ExpectedActivityCode: expectedActivityCode);

        var result = await _activityUpdateProcessor.ProcessRawMessageAsync(submissionRequest, cancellationToken);

        if (result.IsAccepted)
        {
            return Ok(result);
        }

        if (result.ProcessingStatus == MessageProcessingStatus.DuplicateIgnored)
        {
            return Conflict(result);
        }

        return BadRequest(result);
    }

    private async Task<ActionResult<ActivityDailyStatusSnapshotDto>> ExecuteGetDailyStatusAsync(
        string activityCode,
        DateOnly? reportingDate,
        string? zoneCode,
        string? stateCode,
        string? regionCode,
        string? branchCode,
        BranchActivityStatus? status,
        string? searchTerm,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!TryEnsureReadAuthorized(out var authError))
        {
            return authError!;
        }

        try
        {
            var query = new ActivityStatusFilterQuery(
                ActivityCode: activityCode,
                ReportingDate: reportingDate,
                ZoneCode: zoneCode,
                StateCode: stateCode,
                RegionCode: regionCode,
                Status: status,
                SearchTerm: searchTerm,
                BranchCode: branchCode,
                Page: page,
                PageSize: pageSize);

            var snapshot = await _activityStatusQueryService.GetBranchStatusesAsync(query, cancellationToken);
            return Ok(snapshot);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid status query parameters.",
                ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "Activity not found.",
                ex.Message));
        }
    }

    private bool TryEnsureReadAuthorized(out ActionResult? errorResult)
    {
        if (User?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(User.Identity.Name))
        {
            errorResult = Unauthorized(CreateProblem(
                StatusCodes.Status401Unauthorized,
                "Authentication required.",
                "An authenticated user identity is required."));
            return false;
        }

        if (!User.IsInRole(SystemRoles.SuperAdmin) &&
            !User.IsInRole(SystemRoles.Admin) &&
            !User.IsInRole(SystemRoles.Viewer))
        {
            errorResult = Forbid();
            return false;
        }

        errorResult = null;
        return true;
    }

    private bool TryEnsureAdminAuthorized(out string adminUsername, out ActionResult? errorResult)
    {
        if (User?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(User.Identity.Name))
        {
            adminUsername = string.Empty;
            errorResult = Unauthorized(CreateProblem(
                StatusCodes.Status401Unauthorized,
                "Authentication required.",
                "An authenticated administrator identity is required."));
            return false;
        }

        if (!User.IsInRole(SystemRoles.SuperAdmin) && !User.IsInRole(SystemRoles.Admin))
        {
            adminUsername = string.Empty;
            errorResult = Forbid();
            return false;
        }

        adminUsername = User.Identity.Name.Trim();
        errorResult = null;
        return true;
    }

    private static ProblemDetails CreateProblem(int statusCode, string title, string detail) =>
        new()
        {
            Status = statusCode,
            Title = title,
            Detail = detail
        };
}
