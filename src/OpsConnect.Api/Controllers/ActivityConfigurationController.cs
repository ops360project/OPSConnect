using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsConnect.Api.Authentication;
using OpsConnect.Api.Models;
using OpsConnect.Application.Administration;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;

namespace OpsConnect.Api.Controllers;

[ApiController]
[Route("api")]
[Produces("application/json")]
public class ActivityConfigurationController : ControllerBase
{
    private readonly IActivityConfigurationService _activityConfigurationService;

    public ActivityConfigurationController(IActivityConfigurationService activityConfigurationService)
    {
        _activityConfigurationService = activityConfigurationService;
    }

    /// <summary>
    /// Lists all configured operational activities.
    /// </summary>
    [HttpGet("activities")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(IReadOnlyList<ActivitySummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ActivitySummaryDto>>> GetActivities(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryEnsureReadAuthorized(out var authError))
        {
            return authError!;
        }

        var activities = await _activityConfigurationService.GetActivitiesAsync(includeInactive, cancellationToken);
        return Ok(activities);
    }

    /// <summary>
    /// Retrieves full details, key-value settings (with sensitive values masked), and effective reporting configuration for an activity.
    /// </summary>
    [HttpGet("activities/{activityCode:regex(^(?!status$|reopen-permissions$).+)}")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(ActivityDetailsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ActivityDetailsDto>> GetActivityByCode(
        [FromRoute] string activityCode,
        [FromQuery] DateOnly? asOfReportingDate = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryEnsureReadAuthorized(out var authError))
        {
            return authError!;
        }

        if (string.IsNullOrWhiteSpace(activityCode))
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid activity code.",
                "Activity code is required."));
        }

        try
        {
            _ = ActivityMaster.ValidateActivityCode(activityCode);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid activity code.",
                ex.Message));
        }

        var details = await _activityConfigurationService.GetActivityByCodeAsync(
            activityCode,
            asOfReportingDate,
            cancellationToken);

        if (details is null)
        {
            return NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "Activity not found.",
                $"No configured activity was found with code '{activityCode.Trim().ToUpperInvariant()}'."));
        }

        return Ok(details);
    }

    /// <summary>
    /// Creates or updates an ActivityMaster record. Uses the authenticated administrator identity for audit logging.
    /// </summary>
    [HttpPost("activities")]
    [Authorize(Policy = AuthorizationPolicies.ActivityManage)]
    [ProducesResponseType(typeof(ActivityConfigurationOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<ActionResult<ActivityConfigurationOperationResult>> CreateOrUpdateActivity(
        [FromBody] UpsertActivityApiRequest? request,
        CancellationToken cancellationToken = default) =>
        ExecuteUpsertActivityAsync(routeActivityCode: null, request, cancellationToken);

    /// <summary>
    /// Updates an ActivityMaster record by activity code. Uses the authenticated administrator identity for audit logging.
    /// </summary>
    [HttpPut("activities/{activityCode}")]
    [Authorize(Policy = AuthorizationPolicies.ActivityManage)]
    [ProducesResponseType(typeof(ActivityConfigurationOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<ActionResult<ActivityConfigurationOperationResult>> UpdateActivity(
        [FromRoute] string activityCode,
        [FromBody] UpsertActivityApiRequest? request,
        CancellationToken cancellationToken = default) =>
        ExecuteUpsertActivityAsync(routeActivityCode: activityCode, request, cancellationToken);

    /// <summary>
    /// Enables or disables an existing operational activity. Uses the authenticated administrator identity for audit logging.
    /// </summary>
    [HttpPatch("activities/{activityCode}/active-state")]
    [Authorize(Policy = AuthorizationPolicies.ActivityManage)]
    [ProducesResponseType(typeof(ActivityConfigurationOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ActivityConfigurationOperationResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ActivityConfigurationOperationResult>> SetActivityActiveState(
        [FromRoute] string activityCode,
        [FromBody] SetActivityActiveStateApiRequest? request,
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
                "Active state payload is required."));
        }

        try
        {
            var serviceRequest = new SetActivityActiveStateRequest(
                ActivityCode: activityCode,
                IsActive: request.IsActive,
                ChangedBy: adminUser,
                ChangeReason: request.ChangeReason);

            var result = await _activityConfigurationService.SetActivityActiveStateAsync(serviceRequest, cancellationToken);
            if (!result.IsSuccess)
            {
                return result.ErrorCode == "ActivityNotFound"
                    ? NotFound(result)
                    : BadRequest(result);
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid active-state update request.",
                ex.Message));
        }
    }

    /// <summary>
    /// Creates or updates a key-value configuration setting for an activity. Sensitive keys are masked in responses and audit logs.
    /// </summary>
    [HttpPut("activities/{activityCode}/settings/{configKey}")]
    [Authorize(Policy = AuthorizationPolicies.ActivityManage)]
    [ProducesResponseType(typeof(ActivityConfigurationOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ActivityConfigurationOperationResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ActivityConfigurationOperationResult>> SetActivitySetting(
        [FromRoute] string activityCode,
        [FromRoute] string configKey,
        [FromBody] SetActivitySettingApiRequest? request,
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
                "Setting value payload is required."));
        }

        try
        {
            var serviceRequest = new SetActivityKeyValueConfigRequest(
                ActivityCode: activityCode,
                ConfigKey: configKey,
                ConfigValue: request.ConfigValue,
                Description: request.Description,
                ChangedBy: adminUser,
                ChangeReason: request.ChangeReason);

            var result = await _activityConfigurationService.SetActivityConfigurationValueAsync(serviceRequest, cancellationToken);
            if (!result.IsSuccess)
            {
                return result.ErrorCode == "ActivityNotFound"
                    ? NotFound(result)
                    : BadRequest(result);
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid activity setting request.",
                ex.Message));
        }
    }

    /// <summary>
    /// Retrieves the effective reporting configuration as of a specific reporting date along with the full effective-dated schedule history.
    /// </summary>
    [HttpGet("activities/{activityCode}/reporting-schedule")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(ActivityReportingScheduleResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ActivityReportingScheduleResultDto>> GetReportingSchedule(
        [FromRoute] string activityCode,
        [FromQuery] DateOnly? asOfReportingDate = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryEnsureReadAuthorized(out var authError))
        {
            return authError!;
        }

        if (string.IsNullOrWhiteSpace(activityCode))
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid activity code.",
                "Activity code is required."));
        }

        try
        {
            _ = ActivityMaster.ValidateActivityCode(activityCode);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid activity code.",
                ex.Message));
        }

        var schedule = await _activityConfigurationService.GetReportingConfigurationAsync(
            activityCode,
            asOfReportingDate,
            cancellationToken);

        if (schedule is null)
        {
            return NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "Activity not found.",
                $"No configured activity was found with code '{activityCode.Trim().ToUpperInvariant()}'."));
        }

        return Ok(schedule);
    }

    /// <summary>
    /// Creates or updates an effective-dated reporting schedule for an activity.
    /// Validates schedule bounds, timezones, and overlapping effective periods.
    /// </summary>
    [HttpPut("activities/{activityCode}/reporting-schedule")]
    [Authorize(Policy = AuthorizationPolicies.ActivityManage)]
    [ProducesResponseType(typeof(ReportingConfigurationUpdateResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ReportingConfigurationUpdateResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ReportingConfigurationUpdateResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ReportingConfigurationUpdateResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ReportingConfigurationUpdateResult>> UpdateReportingSchedule(
        [FromRoute] string activityCode,
        [FromBody] UpdateReportingScheduleApiRequest? request,
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
                "Reporting schedule update payload is required."));
        }

        try
        {
            var serviceRequest = new UpdateActivityReportingConfigurationRequest(
                ActivityCode: activityCode,
                ReportingStartTime: request.ReportingStartTime,
                ReportingEndTime: request.ReportingEndTime,
                ReportingIntervalMinutes: request.ReportingIntervalMinutes,
                TimeZoneId: request.TimeZoneId,
                IsReportingEnabled: request.IsReportingEnabled,
                StopWhenAllCompleted: request.StopWhenAllCompleted,
                EffectiveFrom: request.EffectiveFrom,
                EffectiveTo: request.EffectiveTo,
                ChangedBy: adminUser,
                ChangeReason: request.ChangeReason,
                AutoClosePreviousOpenPeriod: request.AutoClosePreviousOpenPeriod);

            var result = await _activityConfigurationService.UpdateReportingConfigurationAsync(serviceRequest, cancellationToken);
            if (!result.IsSuccess)
            {
                return result.ErrorCode switch
                {
                    "ActivityNotFound" => NotFound(result),
                    "OverlappingEffectivePeriod" or "MultipleOpenSchedulesNotAllowed" => Conflict(result),
                    _ => BadRequest(result)
                };
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid reporting schedule parameters.",
                ex.Message));
        }
    }

    /// <summary>
    /// Retrieves calculated daily reporting slot times for an activity as of a reporting date,
    /// or previews slot times if custom start/end/interval parameters are supplied.
    /// </summary>
    [HttpGet("activities/{activityCode}/reporting-slots")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(ReportingSlotsPreviewResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ReportingSlotsPreviewResultDto>> GetReportingSlots(
        [FromRoute] string activityCode,
        [FromQuery] DateOnly? asOfReportingDate = null,
        [FromQuery] TimeOnly? reportingStartTime = null,
        [FromQuery] TimeOnly? reportingEndTime = null,
        [FromQuery] int? reportingIntervalMinutes = null,
        [FromQuery] string? timeZoneId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryEnsureReadAuthorized(out var authError))
        {
            return authError!;
        }

        if (reportingStartTime.HasValue || reportingEndTime.HasValue || reportingIntervalMinutes.HasValue)
        {
            if (!reportingStartTime.HasValue || !reportingEndTime.HasValue || !reportingIntervalMinutes.HasValue)
            {
                return BadRequest(CreateProblem(
                    StatusCodes.Status400BadRequest,
                    "Incomplete slot preview parameters.",
                    "When previewing custom slot times, reportingStartTime, reportingEndTime, and reportingIntervalMinutes must all be supplied."));
            }

            try
            {
                var preview = _activityConfigurationService.PreviewReportingSlots(
                    new PreviewReportingSlotsRequest(
                        ReportingStartTime: reportingStartTime.Value,
                        ReportingEndTime: reportingEndTime.Value,
                        ReportingIntervalMinutes: reportingIntervalMinutes.Value,
                        TimeZoneId: string.IsNullOrWhiteSpace(timeZoneId)
                            ? ActivityReportingConfiguration.DefaultTimeZoneId
                            : timeZoneId));

                return Ok(preview);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(CreateProblem(
                    StatusCodes.Status400BadRequest,
                    "Invalid slot preview schedule.",
                    ex.Message));
            }
        }

        var schedule = await _activityConfigurationService.GetReportingConfigurationAsync(
            activityCode,
            asOfReportingDate,
            cancellationToken);

        if (schedule is null)
        {
            return NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "Activity not found.",
                $"No configured activity was found with code '{activityCode.Trim().ToUpperInvariant()}'."));
        }

        var resolved = schedule.ResolvedConfigurationAsOfDate;
        if (resolved is null)
        {
            return NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "Reporting schedule not found.",
                $"No effective reporting configuration was found for activity '{schedule.ActivityCode}' on the requested date."));
        }

        return Ok(new ReportingSlotsPreviewResultDto(
            ReportingStartTime: resolved.ReportingStartTime,
            ReportingEndTime: resolved.ReportingEndTime,
            ReportingIntervalMinutes: resolved.ReportingIntervalMinutes,
            TimeZoneId: resolved.TimeZoneId,
            TotalSlotsCount: resolved.CalculatedDailySlotTimes.Count,
            CalculatedDailySlotTimes: resolved.CalculatedDailySlotTimes));
    }

    /// <summary>
    /// Validates schedule parameters and calculates daily reporting slot times for preview without persisting changes.
    /// </summary>
    [HttpPost("activities/reporting-slots/preview")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(ReportingSlotsPreviewResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<ReportingSlotsPreviewResultDto> PreviewReportingSlots(
        [FromBody] PreviewReportingSlotsApiRequest? request)
    {
        if (!TryEnsureReadAuthorized(out var authError))
        {
            return authError!;
        }

        if (request is null)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Missing request payload.",
                "Schedule preview parameters are required."));
        }

        try
        {
            var preview = _activityConfigurationService.PreviewReportingSlots(
                new PreviewReportingSlotsRequest(
                    ReportingStartTime: request.ReportingStartTime,
                    ReportingEndTime: request.ReportingEndTime,
                    ReportingIntervalMinutes: request.ReportingIntervalMinutes,
                    TimeZoneId: request.TimeZoneId));

            return Ok(preview);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid schedule preview parameters.",
                ex.Message));
        }
    }

    /// <summary>
    /// Retrieves immutable configuration audit history entries with optional entity filtering and pagination.
    /// </summary>
    [HttpGet("configuration-history")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(IReadOnlyList<ConfigurationHistoryEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ConfigurationHistoryEntryDto>>> GetConfigurationHistory(
        [FromQuery] string? entityName = null,
        [FromQuery] string? entityKey = null,
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

        var entries = await _activityConfigurationService.GetConfigurationHistoryAsync(
            entityName,
            entityKey,
            cancellationToken);

        if (page.HasValue || pageSize.HasValue)
        {
            int currentPage = page ?? 1;
            int size = pageSize ?? 100;
            entries = entries
                .Skip((currentPage - 1) * size)
                .Take(size)
                .ToList();
        }

        return Ok(entries);
    }

    /// <summary>
    /// Retrieves immutable configuration audit history entries associated with a specific activity code.
    /// </summary>
    [HttpGet("activities/{activityCode}/configuration-history")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(IReadOnlyList<ConfigurationHistoryEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ConfigurationHistoryEntryDto>>> GetActivityConfigurationHistory(
        [FromRoute] string activityCode,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryEnsureReadAuthorized(out var authError))
        {
            return authError!;
        }

        string normalizedCode;
        try
        {
            normalizedCode = ActivityMaster.ValidateActivityCode(activityCode);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid activity code.",
                ex.Message));
        }

        var allEntries = await _activityConfigurationService.GetConfigurationHistoryAsync(
            entityName: null,
            entityKey: null,
            cancellationToken: cancellationToken);

        IEnumerable<ConfigurationHistoryEntryDto> filtered = allEntries.Where(h =>
            string.Equals(h.EntityKey, normalizedCode, StringComparison.OrdinalIgnoreCase) ||
            h.EntityKey.StartsWith($"{normalizedCode}:", StringComparison.OrdinalIgnoreCase));

        if (page.HasValue || pageSize.HasValue)
        {
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

            int currentPage = page ?? 1;
            int size = pageSize ?? 100;
            filtered = filtered.Skip((currentPage - 1) * size).Take(size);
        }

        return Ok(filtered.ToList());
    }

    private async Task<ActionResult<ActivityConfigurationOperationResult>> ExecuteUpsertActivityAsync(
        string? routeActivityCode,
        UpsertActivityApiRequest? request,
        CancellationToken cancellationToken)
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
                "Activity master configuration payload is required."));
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

        var targetCode = !string.IsNullOrWhiteSpace(routeActivityCode)
            ? routeActivityCode
            : request.ActivityCode;

        if (string.IsNullOrWhiteSpace(targetCode))
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Missing activity code.",
                "ActivityCode must be provided."));
        }

        try
        {
            var serviceRequest = new UpsertActivityMasterRequest(
                ActivityCode: targetCode,
                ActivityName: request.ActivityName,
                Description: request.Description,
                ConfigurationJson: request.ConfigurationJson,
                IsActive: request.IsActive,
                ChangedBy: adminUser,
                ChangeReason: request.ChangeReason);

            var result = await _activityConfigurationService.UpsertActivityAsync(serviceRequest, cancellationToken);
            if (!result.IsSuccess)
            {
                return BadRequest(result);
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid activity configuration request.",
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
