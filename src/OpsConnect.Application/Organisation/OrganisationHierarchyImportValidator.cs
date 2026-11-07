using OpsConnect.Domain.Organisation;

namespace OpsConnect.Application.Organisation;

public sealed class OrganisationHierarchyImportValidator : IOrganisationHierarchyImportValidator
{
    public const int MaxCodeLength = 50;
    public const int MaxHierarchyNameLength = 150;
    public const int MaxBranchNameLength = 200;

    public OrganisationImportValidationReport Validate(IReadOnlyCollection<OrganisationImportRow> rows)
    {
        if (rows is null || rows.Count == 0)
        {
            var emptyError = new OrganisationImportValidationError(
                RowNumber: 0,
                FieldName: "File",
                RawValue: null,
                ErrorCode: OrganisationImportErrorCode.EmptyImportBatch,
                Message: "The import batch contains no organisation hierarchy rows.");

            return new OrganisationImportValidationReport(
                TotalRowsRead: 0,
                ValidRowCount: 0,
                InvalidRowCount: 0,
                IsValid: false,
                DistinctZonesCount: 0,
                DistinctStatesCount: 0,
                DistinctRegionsCount: 0,
                DistinctBranchesCount: 0,
                Errors: new[] { emptyError },
                ValidatedRows: Array.Empty<ValidatedOrganisationHierarchyRow>());
        }

        var errors = new List<OrganisationImportValidationError>();
        var invalidRowNumbers = new HashSet<int>();
        var candidateRows = new List<ValidatedOrganisationHierarchyRow>(rows.Count);

        // Cross-row consistency tracking keyed by normalized code
        var firstZoneDefinitions = new Dictionary<string, (int RowNumber, string ZoneName, bool IsActive)>(StringComparer.OrdinalIgnoreCase);
        var firstStateDefinitions = new Dictionary<string, (int RowNumber, string ZoneCode, string StateName, bool IsActive)>(StringComparer.OrdinalIgnoreCase);
        var firstRegionDefinitions = new Dictionary<string, (int RowNumber, string StateCode, string RegionName, bool IsActive)>(StringComparer.OrdinalIgnoreCase);
        var firstBranchRowByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.OrderBy(r => r.RowNumber))
        {
            int initialErrorCount = errors.Count;

            var zoneCode = ValidateAndNormalizeCode(row.RowNumber, nameof(row.ZoneCode), row.ZoneCode, errors);
            var zoneName = ValidateAndNormalizeName(row.RowNumber, nameof(row.ZoneName), row.ZoneName, MaxHierarchyNameLength, errors);

            var stateCode = ValidateAndNormalizeCode(row.RowNumber, nameof(row.StateCode), row.StateCode, errors);
            var stateName = ValidateAndNormalizeName(row.RowNumber, nameof(row.StateName), row.StateName, MaxHierarchyNameLength, errors);

            var regionCode = ValidateAndNormalizeCode(row.RowNumber, nameof(row.RegionCode), row.RegionCode, errors);
            var regionName = ValidateAndNormalizeName(row.RowNumber, nameof(row.RegionName), row.RegionName, MaxHierarchyNameLength, errors);

            var branchCode = ValidateAndNormalizeCode(row.RowNumber, nameof(row.BranchCode), row.BranchCode, errors);
            var branchName = ValidateAndNormalizeName(row.RowNumber, nameof(row.BranchName), row.BranchName, MaxBranchNameLength, errors);

            // Validate active/inactive parent lifecycle rules
            if (row.IsStateActive && !row.IsZoneActive)
            {
                errors.Add(new OrganisationImportValidationError(
                    row.RowNumber,
                    nameof(row.IsStateActive),
                    row.IsStateActive.ToString(),
                    OrganisationImportErrorCode.InactiveParentHierarchy,
                    $"Active State '{stateCode ?? row.StateCode}' cannot be mapped under inactive Zone '{zoneCode ?? row.ZoneCode}'."));
            }

            if (row.IsRegionActive && (!row.IsStateActive || !row.IsZoneActive))
            {
                errors.Add(new OrganisationImportValidationError(
                    row.RowNumber,
                    nameof(row.IsRegionActive),
                    row.IsRegionActive.ToString(),
                    OrganisationImportErrorCode.InactiveParentHierarchy,
                    $"Active Region '{regionCode ?? row.RegionCode}' cannot be mapped under an inactive parent State or Zone."));
            }

            if (row.IsBranchActive && (!row.IsRegionActive || !row.IsStateActive || !row.IsZoneActive))
            {
                errors.Add(new OrganisationImportValidationError(
                    row.RowNumber,
                    nameof(row.IsBranchActive),
                    row.IsBranchActive.ToString(),
                    OrganisationImportErrorCode.InactiveParentHierarchy,
                    $"Active Branch '{branchCode ?? row.BranchCode}' cannot be mapped under an inactive parent Region, State, or Zone."));
            }

            // Cross-row Zone consistency check
            if (zoneCode is not null && zoneName is not null)
            {
                if (!firstZoneDefinitions.TryGetValue(zoneCode, out var existingZone))
                {
                    firstZoneDefinitions[zoneCode] = (row.RowNumber, zoneName, row.IsZoneActive);
                }
                else if (!string.Equals(existingZone.ZoneName, zoneName, StringComparison.OrdinalIgnoreCase) ||
                         existingZone.IsActive != row.IsZoneActive)
                {
                    errors.Add(new OrganisationImportValidationError(
                        row.RowNumber,
                        nameof(row.ZoneCode),
                        row.ZoneCode,
                        OrganisationImportErrorCode.ConflictingZoneDefinition,
                        $"Zone '{zoneCode}' on row {row.RowNumber} conflicts with definition on row {existingZone.RowNumber} (Name: '{existingZone.ZoneName}', Active: {existingZone.IsActive})."));
                }
            }

            // Cross-row State -> Zone hierarchy consistency check
            if (stateCode is not null && stateName is not null && zoneCode is not null)
            {
                if (!firstStateDefinitions.TryGetValue(stateCode, out var existingState))
                {
                    firstStateDefinitions[stateCode] = (row.RowNumber, zoneCode, stateName, row.IsStateActive);
                }
                else if (!string.Equals(existingState.ZoneCode, zoneCode, StringComparison.OrdinalIgnoreCase) ||
                         !string.Equals(existingState.StateName, stateName, StringComparison.OrdinalIgnoreCase) ||
                         existingState.IsActive != row.IsStateActive)
                {
                    errors.Add(new OrganisationImportValidationError(
                        row.RowNumber,
                        nameof(row.StateCode),
                        row.StateCode,
                        OrganisationImportErrorCode.ConflictingStateHierarchyMapping,
                        $"State '{stateCode}' on row {row.RowNumber} (mapped to Zone '{zoneCode}') conflicts with row {existingState.RowNumber} (mapped to Zone '{existingState.ZoneCode}', Name: '{existingState.StateName}')."));
                }
            }

            // Cross-row Region -> State hierarchy consistency check
            if (regionCode is not null && regionName is not null && stateCode is not null)
            {
                if (!firstRegionDefinitions.TryGetValue(regionCode, out var existingRegion))
                {
                    firstRegionDefinitions[regionCode] = (row.RowNumber, stateCode, regionName, row.IsRegionActive);
                }
                else if (!string.Equals(existingRegion.StateCode, stateCode, StringComparison.OrdinalIgnoreCase) ||
                         !string.Equals(existingRegion.RegionName, regionName, StringComparison.OrdinalIgnoreCase) ||
                         existingRegion.IsActive != row.IsRegionActive)
                {
                    errors.Add(new OrganisationImportValidationError(
                        row.RowNumber,
                        nameof(row.RegionCode),
                        row.RegionCode,
                        OrganisationImportErrorCode.ConflictingRegionHierarchyMapping,
                        $"Region '{regionCode}' on row {row.RowNumber} (mapped to State '{stateCode}') conflicts with row {existingRegion.RowNumber} (mapped to State '{existingRegion.StateCode}', Name: '{existingRegion.RegionName}')."));
                }
            }

            // Cross-row Duplicate BranchCode check
            if (branchCode is not null)
            {
                if (!firstBranchRowByCode.TryGetValue(branchCode, out var firstRowNumber))
                {
                    firstBranchRowByCode[branchCode] = row.RowNumber;
                }
                else
                {
                    errors.Add(new OrganisationImportValidationError(
                        row.RowNumber,
                        nameof(row.BranchCode),
                        row.BranchCode,
                        OrganisationImportErrorCode.DuplicateBranchCode,
                        $"Duplicate BranchCode '{branchCode}' on row {row.RowNumber}; already defined on row {firstRowNumber}."));
                }
            }

            if (errors.Count > initialErrorCount)
            {
                invalidRowNumbers.Add(row.RowNumber);
                continue;
            }

            candidateRows.Add(new ValidatedOrganisationHierarchyRow(
                RowNumber: row.RowNumber,
                ZoneCode: zoneCode!,
                ZoneName: zoneName!,
                IsZoneActive: row.IsZoneActive,
                StateCode: stateCode!,
                StateName: stateName!,
                IsStateActive: row.IsStateActive,
                RegionCode: regionCode!,
                RegionName: regionName!,
                IsRegionActive: row.IsRegionActive,
                BranchCode: branchCode!,
                BranchName: branchName!,
                IsBranchActive: row.IsBranchActive));
        }

        bool isValid = errors.Count == 0 && candidateRows.Count == rows.Count;

        return new OrganisationImportValidationReport(
            TotalRowsRead: rows.Count,
            ValidRowCount: candidateRows.Count,
            InvalidRowCount: invalidRowNumbers.Count,
            IsValid: isValid,
            DistinctZonesCount: firstZoneDefinitions.Count,
            DistinctStatesCount: firstStateDefinitions.Count,
            DistinctRegionsCount: firstRegionDefinitions.Count,
            DistinctBranchesCount: firstBranchRowByCode.Count,
            Errors: errors,
            ValidatedRows: isValid ? candidateRows : Array.Empty<ValidatedOrganisationHierarchyRow>());
    }

    private static string? ValidateAndNormalizeCode(
        int rowNumber,
        string fieldName,
        string? rawCode,
        List<OrganisationImportValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            errors.Add(new OrganisationImportValidationError(
                rowNumber,
                fieldName,
                rawCode,
                OrganisationImportErrorCode.MissingRequiredField,
                $"{fieldName} is required on row {rowNumber}."));
            return null;
        }

        string normalized;
        try
        {
            normalized = OrganisationCodeNormalizer.NormalizeCode(rawCode, fieldName);
        }
        catch (ArgumentException ex)
        {
            errors.Add(new OrganisationImportValidationError(
                rowNumber,
                fieldName,
                rawCode,
                OrganisationImportErrorCode.InvalidCodeFormat,
                ex.Message));
            return null;
        }

        if (normalized.Length > MaxCodeLength)
        {
            errors.Add(new OrganisationImportValidationError(
                rowNumber,
                fieldName,
                rawCode,
                OrganisationImportErrorCode.FieldLengthExceeded,
                $"{fieldName} on row {rowNumber} exceeds maximum length of {MaxCodeLength} characters."));
            return null;
        }

        return normalized;
    }

    private static string? ValidateAndNormalizeName(
        int rowNumber,
        string fieldName,
        string? rawName,
        int maxLength,
        List<OrganisationImportValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            errors.Add(new OrganisationImportValidationError(
                rowNumber,
                fieldName,
                rawName,
                OrganisationImportErrorCode.MissingRequiredField,
                $"{fieldName} is required on row {rowNumber}."));
            return null;
        }

        var normalized = OrganisationCodeNormalizer.NormalizeName(rawName, fieldName);
        if (normalized.Length > maxLength)
        {
            errors.Add(new OrganisationImportValidationError(
                rowNumber,
                fieldName,
                rawName,
                OrganisationImportErrorCode.FieldLengthExceeded,
                $"{fieldName} on row {rowNumber} exceeds maximum length of {maxLength} characters."));
            return null;
        }

        return normalized;
    }
}
