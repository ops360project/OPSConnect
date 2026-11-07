using OpsConnect.Application.Organisation;

namespace OpsConnect.UnitTests;

public class OrganisationImportValidatorTests
{
    private readonly OrganisationHierarchyImportValidator _validator = new();

    [Fact]
    public void Validate_WithValidHierarchyRows_ShouldReturnValidReportAndNormalizedRows()
    {
        var rows = new[]
        {
            new OrganisationImportRow(
                RowNumber: 2,
                ZoneCode: " east ",
                ZoneName: " East Zone ",
                StateCode: " od ",
                StateName: " Odisha ",
                RegionCode: " reg-a ",
                RegionName: " Region A ",
                BranchCode: " 36:01 ",
                BranchName: " Bhubaneswar Main "),
            new OrganisationImportRow(
                RowNumber: 3,
                ZoneCode: "EAST",
                ZoneName: "East Zone",
                StateCode: "OD",
                StateName: "Odisha",
                RegionCode: "REG-A",
                RegionName: "Region A",
                BranchCode: "36:02",
                BranchName: "Cuttack City"),
            new OrganisationImportRow(
                RowNumber: 4,
                ZoneCode: "EAST",
                ZoneName: "East Zone",
                StateCode: "OD",
                StateName: "Odisha",
                RegionCode: "REG-B",
                RegionName: "Region B",
                BranchCode: "36:03",
                BranchName: "Rourkela Branch")
        };

        var report = _validator.Validate(rows);

        Assert.True(report.IsValid);
        Assert.Equal(3, report.TotalRowsRead);
        Assert.Equal(3, report.ValidRowCount);
        Assert.Equal(0, report.InvalidRowCount);
        Assert.Empty(report.Errors);
        Assert.Equal(1, report.DistinctZonesCount);
        Assert.Equal(1, report.DistinctStatesCount);
        Assert.Equal(2, report.DistinctRegionsCount);
        Assert.Equal(3, report.DistinctBranchesCount);

        var firstValidated = report.ValidatedRows[0];
        Assert.Equal("EAST", firstValidated.ZoneCode);
        Assert.Equal("East Zone", firstValidated.ZoneName);
        Assert.Equal("OD", firstValidated.StateCode);
        Assert.Equal("Odisha", firstValidated.StateName);
        Assert.Equal("REG-A", firstValidated.RegionCode);
        Assert.Equal("Region A", firstValidated.RegionName);
        Assert.Equal("36:01", firstValidated.BranchCode);
        Assert.Equal("Bhubaneswar Main", firstValidated.BranchName);
    }

    [Fact]
    public void Validate_WithEmptyRowCollection_ShouldReturnEmptyBatchError()
    {
        var report = _validator.Validate(Array.Empty<OrganisationImportRow>());

        Assert.False(report.IsValid);
        var error = Assert.Single(report.Errors);
        Assert.Equal(OrganisationImportErrorCode.EmptyImportBatch, error.ErrorCode);
    }

    [Fact]
    public void Validate_WithMissingRequiredFieldsAndInvalidCodes_ShouldReportSpecificFieldErrors()
    {
        var rows = new[]
        {
            new OrganisationImportRow(
                RowNumber: 2,
                ZoneCode: "",
                ZoneName: "East Zone",
                StateCode: "OD",
                StateName: "   ",
                RegionCode: "REG A", // invalid internal space
                RegionName: "Region A",
                BranchCode: "36 : 01", // invalid internal space
                BranchName: "")
        };

        var report = _validator.Validate(rows);

        Assert.False(report.IsValid);
        Assert.Equal(1, report.InvalidRowCount);
        Assert.Empty(report.ValidatedRows);

        Assert.Contains(report.Errors, e =>
            e.RowNumber == 2 &&
            e.FieldName == nameof(OrganisationImportRow.ZoneCode) &&
            e.ErrorCode == OrganisationImportErrorCode.MissingRequiredField);

        Assert.Contains(report.Errors, e =>
            e.RowNumber == 2 &&
            e.FieldName == nameof(OrganisationImportRow.StateName) &&
            e.ErrorCode == OrganisationImportErrorCode.MissingRequiredField);

        Assert.Contains(report.Errors, e =>
            e.RowNumber == 2 &&
            e.FieldName == nameof(OrganisationImportRow.RegionCode) &&
            e.ErrorCode == OrganisationImportErrorCode.InvalidCodeFormat);

        Assert.Contains(report.Errors, e =>
            e.RowNumber == 2 &&
            e.FieldName == nameof(OrganisationImportRow.BranchCode) &&
            e.ErrorCode == OrganisationImportErrorCode.InvalidCodeFormat);

        Assert.Contains(report.Errors, e =>
            e.RowNumber == 2 &&
            e.FieldName == nameof(OrganisationImportRow.BranchName) &&
            e.ErrorCode == OrganisationImportErrorCode.MissingRequiredField);
    }

    [Fact]
    public void Validate_WithDuplicateBranchCodes_ShouldDetectCaseInsensitiveAndTrimmedDuplicates()
    {
        var rows = new[]
        {
            new OrganisationImportRow(
                RowNumber: 2,
                ZoneCode: "EAST",
                ZoneName: "East Zone",
                StateCode: "OD",
                StateName: "Odisha",
                RegionCode: "REG-A",
                RegionName: "Region A",
                BranchCode: "36:01",
                BranchName: "Bhubaneswar Main"),
            new OrganisationImportRow(
                RowNumber: 5,
                ZoneCode: "EAST",
                ZoneName: "East Zone",
                StateCode: "OD",
                StateName: "Odisha",
                RegionCode: "REG-B",
                RegionName: "Region B",
                BranchCode: "  36:01  ",
                BranchName: "Duplicate Bhubaneswar")
        };

        var report = _validator.Validate(rows);

        Assert.False(report.IsValid);
        var dupError = Assert.Single(report.Errors);
        Assert.Equal(5, dupError.RowNumber);
        Assert.Equal(OrganisationImportErrorCode.DuplicateBranchCode, dupError.ErrorCode);
        Assert.Contains("row 2", dupError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_WithConflictingStateAndRegionHierarchyMappings_ShouldRejectConflicts()
    {
        var rows = new[]
        {
            new OrganisationImportRow(
                RowNumber: 2,
                ZoneCode: "EAST",
                ZoneName: "East Zone",
                StateCode: "OD",
                StateName: "Odisha",
                RegionCode: "REG-A",
                RegionName: "Region A",
                BranchCode: "36:01",
                BranchName: "Branch 1"),
            // Row 3 maps State OD under Zone SOUTH (conflicts with Row 2 where OD is under EAST)
            new OrganisationImportRow(
                RowNumber: 3,
                ZoneCode: "SOUTH",
                ZoneName: "South Zone",
                StateCode: "OD",
                StateName: "Odisha",
                RegionCode: "REG-B",
                RegionName: "Region B",
                BranchCode: "36:02",
                BranchName: "Branch 2"),
            // Row 4 maps Region REG-A under State WB (conflicts with Row 2 where REG-A is under OD)
            new OrganisationImportRow(
                RowNumber: 4,
                ZoneCode: "EAST",
                ZoneName: "East Zone",
                StateCode: "WB",
                StateName: "West Bengal",
                RegionCode: "REG-A",
                RegionName: "Region A",
                BranchCode: "36:03",
                BranchName: "Branch 3")
        };

        var report = _validator.Validate(rows);

        Assert.False(report.IsValid);
        Assert.Equal(2, report.InvalidRowCount);

        Assert.Contains(report.Errors, e =>
            e.RowNumber == 3 &&
            e.ErrorCode == OrganisationImportErrorCode.ConflictingStateHierarchyMapping);

        Assert.Contains(report.Errors, e =>
            e.RowNumber == 4 &&
            e.ErrorCode == OrganisationImportErrorCode.ConflictingRegionHierarchyMapping);
    }

    [Fact]
    public void Validate_WithActiveChildUnderInactiveParentHierarchy_ShouldReportInactiveParentHierarchyError()
    {
        var rows = new[]
        {
            // Active branch under inactive Region
            new OrganisationImportRow(
                RowNumber: 2,
                ZoneCode: "EAST",
                ZoneName: "East Zone",
                StateCode: "OD",
                StateName: "Odisha",
                RegionCode: "REG-INACTIVE",
                RegionName: "Inactive Region",
                BranchCode: "36:01",
                BranchName: "Active Branch Under Inactive Region",
                IsZoneActive: true,
                IsStateActive: true,
                IsRegionActive: false,
                IsBranchActive: true),
            // Active state under inactive Zone
            new OrganisationImportRow(
                RowNumber: 3,
                ZoneCode: "ZONE-INACTIVE",
                ZoneName: "Inactive Zone",
                StateCode: "WB",
                StateName: "West Bengal",
                RegionCode: "REG-WB",
                RegionName: "Region WB",
                BranchCode: "37:01",
                BranchName: "Branch WB",
                IsZoneActive: false,
                IsStateActive: true,
                IsRegionActive: false,
                IsBranchActive: false)
        };

        var report = _validator.Validate(rows);

        Assert.False(report.IsValid);
        Assert.Equal(2, report.InvalidRowCount);

        Assert.Contains(report.Errors, e =>
            e.RowNumber == 2 &&
            e.FieldName == nameof(OrganisationImportRow.IsBranchActive) &&
            e.ErrorCode == OrganisationImportErrorCode.InactiveParentHierarchy);

        Assert.Contains(report.Errors, e =>
            e.RowNumber == 3 &&
            e.FieldName == nameof(OrganisationImportRow.IsStateActive) &&
            e.ErrorCode == OrganisationImportErrorCode.InactiveParentHierarchy);
    }
}
