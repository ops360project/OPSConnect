namespace OpsConnect.Application.Organisation;

/// <summary>
/// Abstraction for reading organisation hierarchy import rows from an Excel stream.
/// Implemented in OpsConnect.Infrastructure so OpsConnect.Application remains independent of Excel libraries.
/// </summary>
public interface IOrganisationExcelReader
{
    Task<IReadOnlyList<OrganisationImportRow>> ReadRowsAsync(
        Stream excelStream,
        string? sheetName = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates a batch of organisation hierarchy import rows and produces a comprehensive validation report.
/// </summary>
public interface IOrganisationHierarchyImportValidator
{
    OrganisationImportValidationReport Validate(IReadOnlyCollection<OrganisationImportRow> rows);
}

/// <summary>
/// Application service contract for Organisation Master hierarchy imports, queries, and historical mapping resolution.
/// </summary>
public interface IOrganisationMasterService
{
    Task<OrganisationImportExecutionResult> ImportHierarchyFromExcelAsync(
        Stream excelStream,
        DateOnly? effectiveFromDate = null,
        bool validateOnly = false,
        string? importedBy = null,
        string? changeReason = null,
        string? sheetName = null,
        CancellationToken cancellationToken = default);

    Task<OrganisationImportExecutionResult> ImportHierarchyAsync(
        IReadOnlyCollection<OrganisationImportRow> rows,
        DateOnly? effectiveFromDate = null,
        bool validateOnly = false,
        string? importedBy = null,
        string? changeReason = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BranchSummaryDto>> GetBranchesAsync(
        BranchFilterQuery? filter = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ZoneHierarchyNodeDto>> GetHierarchyTreeAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default);

    Task<BranchMappingHistoryResultDto?> GetBranchMappingHistoryAsync(
        string branchCode,
        DateOnly? asOfReportingDate = null,
        CancellationToken cancellationToken = default);
}
