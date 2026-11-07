using MiniExcelLibs;
using OpsConnect.Application.Organisation;
using OpsConnect.Infrastructure.Services;

namespace OpsConnect.IntegrationTests;

public class OrganisationExcelReaderTests
{
    [Fact]
    public async Task ReadRowsAsync_ShouldParseSpreadsheetWithFlexibleHeadersAndSkipBlankRows()
    {
        var sheetRows = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["Zone Code"] = "EAST",
                ["Zone Name"] = "East Zone",
                ["State Code"] = "OD",
                ["State Name"] = "Odisha",
                ["Region Code"] = "REG-A",
                ["Region Name"] = "Region A",
                ["Branch ID"] = "36:01",
                ["Branch Name"] = "Bhubaneswar Main",
                ["Status"] = "Active"
            },
            // Completely blank row in Excel (Row 3) should be skipped
            new()
            {
                ["Zone Code"] = null,
                ["Zone Name"] = "   ",
                ["State Code"] = null,
                ["State Name"] = null,
                ["Region Code"] = null,
                ["Region Name"] = null,
                ["Branch ID"] = null,
                ["Branch Name"] = null,
                ["Status"] = null
            },
            // Second valid branch on Excel Row 4 (inactive branch)
            new()
            {
                ["Zone Code"] = "EAST",
                ["Zone Name"] = "East Zone",
                ["State Code"] = "OD",
                ["State Name"] = "Odisha",
                ["Region Code"] = "REG-A",
                ["Region Name"] = "Region A",
                ["Branch ID"] = "36:02",
                ["Branch Name"] = "Old Cuttack Sub-Branch",
                ["Status"] = "Inactive"
            }
        };

        await using var stream = new MemoryStream();
        await stream.SaveAsAsync(sheetRows);
        stream.Position = 0;

        var reader = new OrganisationExcelReader();
        var validator = new OrganisationHierarchyImportValidator();

        var importedRows = await reader.ReadRowsAsync(stream);

        Assert.Equal(2, importedRows.Count);

        Assert.Equal(2, importedRows[0].RowNumber);
        Assert.Equal("36:01", importedRows[0].BranchCode);
        Assert.True(importedRows[0].IsBranchActive);

        Assert.Equal(4, importedRows[1].RowNumber);
        Assert.Equal("36:02", importedRows[1].BranchCode);
        Assert.False(importedRows[1].IsBranchActive);

        var validationReport = validator.Validate(importedRows);
        Assert.True(validationReport.IsValid);
        Assert.Equal(2, validationReport.ValidRowCount);
    }
}
