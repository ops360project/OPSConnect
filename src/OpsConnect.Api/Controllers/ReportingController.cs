using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsConnect.Api.Authentication;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Reporting;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;

namespace OpsConnect.Api.Controllers;

[ApiController]
[Route("api/reports")]
[Produces("application/json")]
public class ReportingController : ControllerBase
{
    private readonly IActivityReportingService _reportingService;
    private readonly IScheduledReportingService? _scheduledReportingService;

    public ReportingController(
        IActivityReportingService reportingService,
        IScheduledReportingService? scheduledReportingService = null)
    {
        _reportingService = reportingService;
        _scheduledReportingService = scheduledReportingService;
    }

    /// <summary>
    /// Retrieves top-level activity summary report with zone breakdowns for the specified activity and reporting date.
    /// </summary>
    [HttpGet("{activityCode}/summary")]
    [HttpGet("summary")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(ActivityReportSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ActivityReportSummaryDto>> GetSummary(
        [FromRoute] string? activityCode = null,
        [FromQuery] string? queryActivityCode = null,
        [FromQuery] DateOnly? reportingDate = null,
        [FromQuery] string? zoneCode = null,
        [FromQuery] string? stateCode = null,
        [FromQuery] string? regionCode = null,
        CancellationToken cancellationToken = default)
    {
        var authCheck = ValidateAuthorization();
        if (authCheck is not null)
        {
            return authCheck;
        }

        try
        {
            var query = new ReportingFilterQuery
            {
                ActivityCode = ResolveActivityCode(activityCode, queryActivityCode),
                ReportingDate = reportingDate,
                ZoneCode = zoneCode,
                StateCode = stateCode,
                RegionCode = regionCode
            };

            var report = await _reportingService.GetActivitySummaryReportAsync(query, cancellationToken);
            return Ok(report);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Activity Not Found",
                Detail = ex.Message
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid Reporting Query",
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Retrieves aggregated summaries for Zones for the specified activity and reporting date.
    /// </summary>
    [HttpGet("{activityCode}/zones")]
    [HttpGet("zones")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(IReadOnlyList<ZoneReportSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ZoneReportSummaryDto>>> GetZones(
        [FromRoute] string? activityCode = null,
        [FromQuery] string? queryActivityCode = null,
        [FromQuery] DateOnly? reportingDate = null,
        [FromQuery] string? zoneCode = null,
        CancellationToken cancellationToken = default)
    {
        var authCheck = ValidateAuthorization();
        if (authCheck is not null)
        {
            return authCheck;
        }

        try
        {
            var query = new ReportingFilterQuery
            {
                ActivityCode = ResolveActivityCode(activityCode, queryActivityCode),
                ReportingDate = reportingDate,
                ZoneCode = zoneCode
            };

            var zones = await _reportingService.GetZoneSummariesAsync(query, cancellationToken);
            return Ok(zones);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Activity Not Found",
                Detail = ex.Message
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid Reporting Query",
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Retrieves aggregated summaries for States for the specified activity and reporting date.
    /// </summary>
    [HttpGet("{activityCode}/states")]
    [HttpGet("states")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(IReadOnlyList<StateReportSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<StateReportSummaryDto>>> GetStates(
        [FromRoute] string? activityCode = null,
        [FromQuery] string? queryActivityCode = null,
        [FromQuery] DateOnly? reportingDate = null,
        [FromQuery] string? zoneCode = null,
        [FromQuery] string? stateCode = null,
        CancellationToken cancellationToken = default)
    {
        var authCheck = ValidateAuthorization();
        if (authCheck is not null)
        {
            return authCheck;
        }

        try
        {
            var query = new ReportingFilterQuery
            {
                ActivityCode = ResolveActivityCode(activityCode, queryActivityCode),
                ReportingDate = reportingDate,
                ZoneCode = zoneCode,
                StateCode = stateCode
            };

            var states = await _reportingService.GetStateSummariesAsync(query, cancellationToken);
            return Ok(states);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Activity Not Found",
                Detail = ex.Message
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid Reporting Query",
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Retrieves aggregated summaries for Regions for the specified activity and reporting date.
    /// </summary>
    [HttpGet("{activityCode}/regions")]
    [HttpGet("regions")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(IReadOnlyList<RegionReportSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<RegionReportSummaryDto>>> GetRegions(
        [FromRoute] string? activityCode = null,
        [FromQuery] string? queryActivityCode = null,
        [FromQuery] DateOnly? reportingDate = null,
        [FromQuery] string? zoneCode = null,
        [FromQuery] string? stateCode = null,
        [FromQuery] string? regionCode = null,
        CancellationToken cancellationToken = default)
    {
        var authCheck = ValidateAuthorization();
        if (authCheck is not null)
        {
            return authCheck;
        }

        try
        {
            var query = new ReportingFilterQuery
            {
                ActivityCode = ResolveActivityCode(activityCode, queryActivityCode),
                ReportingDate = reportingDate,
                ZoneCode = zoneCode,
                StateCode = stateCode,
                RegionCode = regionCode
            };

            var regions = await _reportingService.GetRegionSummariesAsync(query, cancellationToken);
            return Ok(regions);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Activity Not Found",
                Detail = ex.Message
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid Reporting Query",
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Retrieves detailed branch-wise reporting status records with optional filtering and pagination.
    /// </summary>
    [HttpGet("{activityCode}/branches")]
    [HttpGet("branches")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(BranchReportDetailsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BranchReportDetailsResponseDto>> GetBranches(
        [FromRoute] string? activityCode = null,
        [FromQuery] string? queryActivityCode = null,
        [FromQuery] DateOnly? reportingDate = null,
        [FromQuery] string? zoneCode = null,
        [FromQuery] string? stateCode = null,
        [FromQuery] string? regionCode = null,
        [FromQuery] string? branchCode = null,
        [FromQuery] BranchActivityStatus? status = null,
        [FromQuery] string? searchTerm = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var authCheck = ValidateAuthorization();
        if (authCheck is not null)
        {
            return authCheck;
        }

        try
        {
            var query = new ReportingFilterQuery
            {
                ActivityCode = ResolveActivityCode(activityCode, queryActivityCode),
                ReportingDate = reportingDate,
                ZoneCode = zoneCode,
                StateCode = stateCode,
                RegionCode = regionCode,
                BranchCode = branchCode,
                Status = status,
                SearchTerm = searchTerm,
                Page = page,
                PageSize = pageSize
            };

            var branches = await _reportingService.GetBranchReportDetailsAsync(query, cancellationToken);
            return Ok(branches);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Activity Not Found",
                Detail = ex.Message
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid Reporting Query",
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Retrieves durable scheduled reporting slots and delivery statuses for the specified activity and reporting date.
    /// </summary>
    [HttpGet("{activityCode}/schedule")]
    [HttpGet("schedule")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(IReadOnlyList<ScheduledSlotStatusDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ScheduledSlotStatusDto>>> GetSchedule(
        [FromRoute] string? activityCode = null,
        [FromQuery] string? queryActivityCode = null,
        [FromQuery] DateOnly? reportingDate = null,
        CancellationToken cancellationToken = default)
    {
        var authCheck = ValidateAuthorization();
        if (authCheck is not null)
        {
            return authCheck;
        }

        if (_scheduledReportingService is null)
        {
            return Ok(Array.Empty<ScheduledSlotStatusDto>());
        }

        try
        {
            string resolvedCode = ResolveActivityCode(activityCode, queryActivityCode);
            var slots = await _scheduledReportingService.GetScheduledSlotsAsync(resolvedCode, reportingDate, cancellationToken);
            return Ok(slots);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Activity Not Found",
                Detail = ex.Message
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid Reporting Query",
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Retrieves durable reporting cycle status for the specified activity and reporting date.
    /// </summary>
    [HttpGet("{activityCode}/cycle-status")]
    [HttpGet("cycle-status")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(ScheduledReportingCycleStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ScheduledReportingCycleStatusDto>> GetCycleStatus(
        [FromRoute] string? activityCode = null,
        [FromQuery] string? queryActivityCode = null,
        [FromQuery] DateOnly? reportingDate = null,
        CancellationToken cancellationToken = default)
    {
        var authCheck = ValidateAuthorization();
        if (authCheck is not null)
        {
            return authCheck;
        }

        if (_scheduledReportingService is null)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Reporting Service Unavailable",
                Detail = "Scheduled reporting service is not available."
            });
        }

        try
        {
            string resolvedCode = ResolveActivityCode(activityCode, queryActivityCode);
            var cycle = await _scheduledReportingService.GetReportingCycleStatusAsync(resolvedCode, reportingDate, cancellationToken);
            if (cycle is null)
            {
                return NotFound(new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "Reporting Cycle Not Found",
                    Detail = $"No reporting cycle found for activity '{resolvedCode}' on {reportingDate}."
                });
            }

            return Ok(cycle);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Activity Not Found",
                Detail = ex.Message
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid Reporting Query",
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Triggers immediate execution of due scheduled reports for all active activities or a specific activity.
    /// </summary>
    [HttpPost("{activityCode}/dispatch")]
    [HttpPost("dispatch")]
    [Authorize(Policy = AuthorizationPolicies.ActivityManage)]
    [ProducesResponseType(typeof(ScheduledReportingDispatchResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ScheduledReportingDispatchResult>> TriggerDispatch(
        [FromRoute] string? activityCode = null,
        [FromQuery] string? queryActivityCode = null,
        CancellationToken cancellationToken = default)
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return Unauthorized();
        }

        if (!User.IsInRole(SystemRoles.SuperAdmin) && !User.IsInRole(SystemRoles.Admin))
        {
            return Forbid();
        }

        if (_scheduledReportingService is null)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Service Unavailable",
                Detail = "Scheduled reporting service is not configured."
            });
        }

        var result = await _scheduledReportingService.DispatchDueReportsAsync(asOfUtc: null, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Evaluates whether all branches have completed and immediately generates and delivers the final report.
    /// </summary>
    [HttpPost("{activityCode}/dispatch-final")]
    [HttpPost("dispatch-final")]
    [Authorize(Policy = AuthorizationPolicies.ActivityManage)]
    [ProducesResponseType(typeof(ImmediateFinalReportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ImmediateFinalReportResult>> TriggerFinalDispatch(
        [FromRoute] string? activityCode = null,
        [FromQuery] string? queryActivityCode = null,
        [FromQuery] DateOnly? reportingDate = null,
        CancellationToken cancellationToken = default)
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return Unauthorized();
        }

        if (!User.IsInRole(SystemRoles.SuperAdmin) && !User.IsInRole(SystemRoles.Admin))
        {
            return Forbid();
        }

        if (_scheduledReportingService is null)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Service Unavailable",
                Detail = "Scheduled reporting service is not configured."
            });
        }

        try
        {
            string resolvedCode = ResolveActivityCode(activityCode, queryActivityCode);
            var result = await _scheduledReportingService.ExecuteImmediateFinalReportAsync(
                resolvedCode,
                reportingDate,
                cancellationToken);

            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Activity Not Found",
                Detail = ex.Message
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid Request",
                Detail = ex.Message
            });
        }
    }

    private ActionResult? ValidateAuthorization()
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return Unauthorized();
        }

        if (!User.IsInRole(SystemRoles.SuperAdmin) &&
            !User.IsInRole(SystemRoles.Admin) &&
            !User.IsInRole(SystemRoles.Viewer))
        {
            return Forbid();
        }

        return null;
    }

    private static string ResolveActivityCode(string? routeActivityCode, string? queryActivityCode)
    {
        if (!string.IsNullOrWhiteSpace(routeActivityCode))
        {
            return routeActivityCode.Trim();
        }

        if (!string.IsNullOrWhiteSpace(queryActivityCode))
        {
            return queryActivityCode.Trim();
        }

        return ActivityModuleConstants.RffoActivityCode;
    }
}
