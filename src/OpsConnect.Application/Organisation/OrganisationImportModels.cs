namespace OpsConnect.Application.Organisation;

public enum OrganisationImportErrorCode
{
    EmptyImportBatch = 1,
    MissingRequiredField = 2,
    InvalidCodeFormat = 3,
    FieldLengthExceeded = 4,
    DuplicateBranchCode = 5,
    ConflictingZoneDefinition = 6,
    ConflictingStateHierarchyMapping = 7,
    ConflictingRegionHierarchyMapping = 8,
    InactiveParentHierarchy = 9
}

/// <summary>
/// Represents a single raw row read from an organisation hierarchy import source (e.g., Excel).
/// RowNumber corresponds to the 1-based spreadsheet row number (where header is row 1 and data starts at row 2).
/// </summary>
public sealed record OrganisationImportRow(
    int RowNumber,
    string? ZoneCode,
    string? ZoneName,
    string? StateCode,
    string? StateName,
    string? RegionCode,
    string? RegionName,
    string? BranchCode,
    string? BranchName,
    bool IsZoneActive = true,
    bool IsStateActive = true,
    bool IsRegionActive = true,
    bool IsBranchActive = true);

public sealed record OrganisationImportValidationError(
    int RowNumber,
    string FieldName,
    string? RawValue,
    OrganisationImportErrorCode ErrorCode,
    string Message);

public sealed record ValidatedOrganisationHierarchyRow(
    int RowNumber,
    string ZoneCode,
    string ZoneName,
    bool IsZoneActive,
    string StateCode,
    string StateName,
    bool IsStateActive,
    string RegionCode,
    string RegionName,
    bool IsRegionActive,
    string BranchCode,
    string BranchName,
    bool IsBranchActive);

public sealed record OrganisationImportValidationReport(
    int TotalRowsRead,
    int ValidRowCount,
    int InvalidRowCount,
    bool IsValid,
    int DistinctZonesCount,
    int DistinctStatesCount,
    int DistinctRegionsCount,
    int DistinctBranchesCount,
    IReadOnlyList<OrganisationImportValidationError> Errors,
    IReadOnlyList<ValidatedOrganisationHierarchyRow> ValidatedRows);
