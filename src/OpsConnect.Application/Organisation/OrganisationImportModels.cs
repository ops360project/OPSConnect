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
    InactiveParentHierarchy = 9,
    InvalidEffectiveDateTransition = 10
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

public sealed record OrganisationImportExecutionResult(
    bool ValidateOnly,
    bool WasPersisted,
    DateOnly EffectiveFromDate,
    OrganisationImportValidationReport ValidationReport,
    int ZonesCreated = 0,
    int ZonesUpdated = 0,
    int StatesCreated = 0,
    int StatesUpdated = 0,
    int RegionsCreated = 0,
    int RegionsUpdated = 0,
    int BranchesCreated = 0,
    int BranchesUpdated = 0,
    int MappingHistoriesCreated = 0);

public sealed record BranchFilterQuery(
    bool? IsActive = null,
    string? ZoneCode = null,
    string? StateCode = null,
    string? RegionCode = null,
    string? SearchTerm = null);

public sealed record BranchSummaryDto(
    int BranchId,
    string BranchCode,
    string BranchName,
    bool IsActive,
    int RegionId,
    string RegionCode,
    string RegionName,
    bool IsRegionActive,
    int StateId,
    string StateCode,
    string StateName,
    bool IsStateActive,
    int ZoneId,
    string ZoneCode,
    string ZoneName,
    bool IsZoneActive,
    bool CanSubmitActivityUpdates,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

public sealed record BranchHierarchyNodeDto(
    int BranchId,
    string BranchCode,
    string BranchName,
    bool IsActive);

public sealed record RegionHierarchyNodeDto(
    int RegionId,
    string RegionCode,
    string RegionName,
    bool IsActive,
    IReadOnlyList<BranchHierarchyNodeDto> Branches);

public sealed record StateHierarchyNodeDto(
    int StateId,
    string StateCode,
    string StateName,
    bool IsActive,
    IReadOnlyList<RegionHierarchyNodeDto> Regions);

public sealed record ZoneHierarchyNodeDto(
    int ZoneId,
    string ZoneCode,
    string ZoneName,
    bool IsActive,
    IReadOnlyList<StateHierarchyNodeDto> States);

public sealed record BranchMappingHistoryEntryDto(
    long MappingHistoryId,
    int BranchId,
    string BranchCode,
    int ZoneId,
    string ZoneCode,
    string ZoneName,
    int StateId,
    string StateCode,
    string StateName,
    int RegionId,
    string RegionCode,
    string RegionName,
    DateOnly EffectiveFromDate,
    DateOnly? EffectiveToDate,
    string? ChangeReason,
    DateTime RecordedAtUtc,
    string? RecordedBy,
    bool IsCurrentActiveMapping);

public sealed record BranchMappingHistoryResultDto(
    int BranchId,
    string BranchCode,
    string BranchName,
    bool IsBranchActive,
    DateOnly? AsOfReportingDate,
    BranchMappingHistoryEntryDto? ResolvedMappingAsOfDate,
    IReadOnlyList<BranchMappingHistoryEntryDto> HistoryEntries);
