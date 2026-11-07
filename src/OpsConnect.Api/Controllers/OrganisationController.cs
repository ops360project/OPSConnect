using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsConnect.Api.Authentication;
using OpsConnect.Application.Organisation;

namespace OpsConnect.Api.Controllers;

[ApiController]
[Route("api")]
[Produces("application/json")]
public class OrganisationController : ControllerBase
{
    private readonly IOrganisationMasterService _organisationMasterService;

    public OrganisationController(IOrganisationMasterService organisationMasterService)
    {
        _organisationMasterService = organisationMasterService;
    }

    /// <summary>
    /// Retrieves configured branches with optional hierarchy and active-status filters.
    /// </summary>
    [HttpGet("branches")]
    [Authorize(Policy = AuthorizationPolicies.OrganisationRead)]
    [ProducesResponseType(typeof(IReadOnlyList<BranchSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<BranchSummaryDto>>> GetBranches(
        [FromQuery] bool? isActive = null,
        [FromQuery] string? zoneCode = null,
        [FromQuery] string? stateCode = null,
        [FromQuery] string? regionCode = null,
        [FromQuery] string? searchTerm = null,
        CancellationToken cancellationToken = default)
    {
        var filter = new BranchFilterQuery(
            IsActive: isActive,
            ZoneCode: zoneCode,
            StateCode: stateCode,
            RegionCode: regionCode,
            SearchTerm: searchTerm);

        var branches = await _organisationMasterService.GetBranchesAsync(filter, cancellationToken);
        return Ok(branches);
    }

    /// <summary>
    /// Retrieves the full Zone -> State -> Region -> Branch hierarchy tree.
    /// </summary>
    [HttpGet("hierarchy")]
    [Authorize(Policy = AuthorizationPolicies.OrganisationRead)]
    [ProducesResponseType(typeof(IReadOnlyList<ZoneHierarchyNodeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ZoneHierarchyNodeDto>>> GetHierarchy(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var tree = await _organisationMasterService.GetHierarchyTreeAsync(includeInactive, cancellationToken);
        return Ok(tree);
    }

    /// <summary>
    /// Retrieves the effective-dated hierarchy mapping history for a branch and resolves its mapping as of a specific reporting date.
    /// </summary>
    [HttpGet("branches/{branchCode}/history")]
    [Authorize(Policy = AuthorizationPolicies.OrganisationRead)]
    [ProducesResponseType(typeof(BranchMappingHistoryResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BranchMappingHistoryResultDto>> GetBranchMappingHistory(
        [FromRoute] string branchCode,
        [FromQuery] DateOnly? asOfReportingDate = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(branchCode))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid branch code.",
                Detail = "Branch code is required."
            });
        }

        var historyResult = await _organisationMasterService.GetBranchMappingHistoryAsync(
            branchCode,
            asOfReportingDate,
            cancellationToken);

        if (historyResult is null)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Branch not found.",
                Detail = $"No configured branch was found with code '{branchCode.Trim().ToUpperInvariant()}'."
            });
        }

        return Ok(historyResult);
    }

    /// <summary>
    /// Validates and imports an organisation hierarchy from an uploaded Excel (.xlsx) file.
    /// Supports validation-only dry runs via validateOnly=true and custom effective dates via effectiveFromDate.
    /// </summary>
    [HttpPost("hierarchy/import")]
    [Authorize(Policy = AuthorizationPolicies.OrganisationManage)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(OrganisationImportExecutionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OrganisationImportExecutionResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OrganisationImportExecutionResult>> ImportHierarchy(
        IFormFile? file,
        [FromQuery] bool validateOnly = false,
        [FromQuery] DateOnly? effectiveFromDate = null,
        [FromQuery] string? changeReason = null,
        [FromQuery] string? sheetName = null,
        CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Missing or empty Excel file.",
                Detail = "An Excel (.xlsx) file must be uploaded and cannot be empty."
            });
        }

        var extension = Path.GetExtension(file.FileName);
        if (!string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Unsupported file format.",
                Detail = "Only .xlsx Excel spreadsheets are supported for organisation hierarchy import."
            });
        }

        await using var stream = file.OpenReadStream();
        var importedBy = User.Identity?.Name ?? "AdminApi";

        var result = await _organisationMasterService.ImportHierarchyFromExcelAsync(
            excelStream: stream,
            effectiveFromDate: effectiveFromDate,
            validateOnly: validateOnly,
            importedBy: importedBy,
            changeReason: changeReason,
            sheetName: sheetName,
            cancellationToken: cancellationToken);

        if (!result.ValidationReport.IsValid)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }
}
