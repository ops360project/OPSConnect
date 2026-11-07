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
