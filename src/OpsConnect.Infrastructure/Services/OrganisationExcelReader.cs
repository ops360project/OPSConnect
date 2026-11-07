using MiniExcelLibs;
using OpsConnect.Application.Organisation;

namespace OpsConnect.Infrastructure.Services;

public sealed class OrganisationExcelReader : IOrganisationExcelReader
{
    public Task<IReadOnlyList<OrganisationImportRow>> ReadRowsAsync(
        Stream excelStream,
        string? sheetName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(excelStream);
        cancellationToken.ThrowIfCancellationRequested();

        var rawRows = excelStream
            .Query(useHeaderRow: true, sheetName: sheetName)
            .Cast<IDictionary<string, object?>>()
            .ToList();

        var results = new List<OrganisationImportRow>(rawRows.Count);

        for (int index = 0; index < rawRows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rawRow = rawRows[index];
            if (IsCompletelyBlankRow(rawRow))
            {
                continue;
            }

            // Header row is row 1; first data row is row 2
            int excelRowNumber = index + 2;
            var normalizedLookup = BuildNormalizedHeaderLookup(rawRow);

            var zoneCode = GetStringValue(normalizedLookup, "zonecode", "zoneid", "zone");
            var zoneName = GetStringValue(normalizedLookup, "zonename", "zone");

            var stateCode = GetStringValue(normalizedLookup, "statecode", "stateid", "state");
            var stateName = GetStringValue(normalizedLookup, "statename", "state");

            var regionCode = GetStringValue(normalizedLookup, "regioncode", "regionid", "region");
            var regionName = GetStringValue(normalizedLookup, "regionname", "region");

            var branchCode = GetStringValue(normalizedLookup, "branchcode", "branchid", "branch");
            var branchName = GetStringValue(normalizedLookup, "branchname");

            var isZoneActive = GetBooleanValue(normalizedLookup, defaultValue: true, "iszoneactive", "zoneactive", "zonestatus");
            var isStateActive = GetBooleanValue(normalizedLookup, defaultValue: true, "isstateactive", "stateactive", "statestatus");
            var isRegionActive = GetBooleanValue(normalizedLookup, defaultValue: true, "isregionactive", "regionactive", "regionstatus");
            var isBranchActive = GetBooleanValue(
                normalizedLookup,
                defaultValue: true,
                "isbranchactive",
                "branchactive",
                "branchstatus",
                "isactive",
                "active",
                "status");

            results.Add(new OrganisationImportRow(
                RowNumber: excelRowNumber,
                ZoneCode: zoneCode,
                ZoneName: zoneName,
                StateCode: stateCode,
                StateName: stateName,
                RegionCode: regionCode,
                RegionName: regionName,
                BranchCode: branchCode,
                BranchName: branchName,
                IsZoneActive: isZoneActive,
                IsStateActive: isStateActive,
                IsRegionActive: isRegionActive,
                IsBranchActive: isBranchActive));
        }

        return Task.FromResult<IReadOnlyList<OrganisationImportRow>>(results);
    }

    private static bool IsCompletelyBlankRow(IDictionary<string, object?> row)
    {
        foreach (var value in row.Values)
        {
            if (value is not null && !string.IsNullOrWhiteSpace(value.ToString()))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, object?> BuildNormalizedHeaderLookup(IDictionary<string, object?> row)
    {
        var lookup = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in row)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key))
            {
                continue;
            }

            var normalizedKey = NormalizeHeaderName(kvp.Key);
            if (!string.IsNullOrEmpty(normalizedKey))
            {
                lookup[normalizedKey] = kvp.Value;
            }
        }

        return lookup;
    }

    private static string NormalizeHeaderName(string header)
    {
        Span<char> buffer = stackalloc char[header.Length];
        int length = 0;

        foreach (char c in header)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }

    private static string? GetStringValue(Dictionary<string, object?> lookup, params string[] candidateKeys)
    {
        foreach (var key in candidateKeys)
        {
            if (lookup.TryGetValue(key, out var rawValue) && rawValue is not null)
            {
                return rawValue.ToString();
            }
        }

        return null;
    }

    private static bool GetBooleanValue(
        Dictionary<string, object?> lookup,
        bool defaultValue,
        params string[] candidateKeys)
    {
        foreach (var key in candidateKeys)
        {
            if (!lookup.TryGetValue(key, out var rawValue) || rawValue is null)
            {
                continue;
            }

            if (rawValue is bool boolVal)
            {
                return boolVal;
            }

            var text = rawValue.ToString()?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (bool.TryParse(text, out var parsedBool))
            {
                return parsedBool;
            }

            if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "Y", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "YES", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "N", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "NO", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "INACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return defaultValue;
    }
}
