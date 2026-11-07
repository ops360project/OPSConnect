using Microsoft.EntityFrameworkCore;
using OpsConnect.Application.Common;
using OpsConnect.Application.Organisation;
using OpsConnect.Domain.Organisation;
using OpsConnect.Infrastructure.Persistence;

namespace OpsConnect.Infrastructure.Services;

public sealed class OrganisationMasterService : IOrganisationMasterService
{
    private readonly OpsConnectDbContext _dbContext;
    private readonly IOrganisationExcelReader _excelReader;
    private readonly IOrganisationHierarchyImportValidator _validator;
    private readonly IBusinessClock _businessClock;

    public OrganisationMasterService(
        OpsConnectDbContext dbContext,
        IOrganisationExcelReader excelReader,
        IOrganisationHierarchyImportValidator validator,
        IBusinessClock businessClock)
    {
        _dbContext = dbContext;
        _excelReader = excelReader;
        _validator = validator;
        _businessClock = businessClock;
    }

    public async Task<OrganisationImportExecutionResult> ImportHierarchyFromExcelAsync(
        Stream excelStream,
        DateOnly? effectiveFromDate = null,
        bool validateOnly = false,
        string? importedBy = null,
        string? changeReason = null,
        string? sheetName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(excelStream);

        var rows = await _excelReader.ReadRowsAsync(excelStream, sheetName, cancellationToken);
        return await ImportHierarchyAsync(
            rows: rows,
            effectiveFromDate: effectiveFromDate,
            validateOnly: validateOnly,
            importedBy: importedBy,
            changeReason: changeReason,
            cancellationToken: cancellationToken);
    }

    public async Task<OrganisationImportExecutionResult> ImportHierarchyAsync(
        IReadOnlyCollection<OrganisationImportRow> rows,
        DateOnly? effectiveFromDate = null,
        bool validateOnly = false,
        string? importedBy = null,
        string? changeReason = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveDate = effectiveFromDate ?? _businessClock.GetBusinessDate("Asia/Kolkata");
        var validationReport = _validator.Validate(rows);

        if (!validationReport.IsValid)
        {
            return new OrganisationImportExecutionResult(
                ValidateOnly: validateOnly,
                WasPersisted: false,
                EffectiveFromDate: effectiveDate,
                ValidationReport: validationReport);
        }

        // Pre-validate effective-date transitions against existing database records before any write
        var preCheckReport = await ValidateAgainstExistingDatabaseStateAsync(
            validationReport,
            effectiveDate,
            cancellationToken);

        if (!preCheckReport.IsValid || validateOnly)
        {
            return new OrganisationImportExecutionResult(
                ValidateOnly: validateOnly,
                WasPersisted: false,
                EffectiveFromDate: effectiveDate,
                ValidationReport: preCheckReport);
        }

        var ambientTransaction = _dbContext.Database.CurrentTransaction;
        if (ambientTransaction is not null)
        {
            const string savepointName = "OrgHierarchyImportSavepoint";
            await ambientTransaction.CreateSavepointAsync(savepointName, cancellationToken);
            try
            {
                var result = await ExecuteImportCoreAsync(
                    preCheckReport,
                    effectiveDate,
                    importedBy,
                    changeReason,
                    cancellationToken);

                await ambientTransaction.ReleaseSavepointAsync(savepointName, cancellationToken);
                return result;
            }
            catch
            {
                await ambientTransaction.RollbackToSavepointAsync(savepointName, cancellationToken);
                _dbContext.ChangeTracker.Clear();
                throw;
            }
        }

        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var result = await ExecuteImportCoreAsync(
                    preCheckReport,
                    effectiveDate,
                    importedBy,
                    changeReason,
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                _dbContext.ChangeTracker.Clear();
                throw;
            }
        });
    }

    private async Task<OrganisationImportValidationReport> ValidateAgainstExistingDatabaseStateAsync(
        OrganisationImportValidationReport batchReport,
        DateOnly effectiveDate,
        CancellationToken cancellationToken)
    {
        var branchCodes = batchReport.ValidatedRows
            .Select(r => r.BranchCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingBranches = await _dbContext.Branches
            .AsNoTracking()
            .Include(b => b.MappingHistories)
            .Where(b => branchCodes.Contains(b.BranchCode))
            .ToDictionaryAsync(b => b.BranchCode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        if (existingBranches.Count == 0)
        {
            return batchReport;
        }

        var additionalErrors = new List<OrganisationImportValidationError>();
        var invalidRowNumbers = new HashSet<int>();

        foreach (var row in batchReport.ValidatedRows)
        {
            if (!existingBranches.TryGetValue(row.BranchCode, out var existingBranch))
            {
                continue;
            }

            var activeHistory = existingBranch.MappingHistories.SingleOrDefault(h => h.EffectiveToDate == null);
            if (activeHistory is null)
            {
                continue;
            }

            bool hierarchyWillChange =
                !string.Equals(activeHistory.ZoneCode, row.ZoneCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(activeHistory.StateCode, row.StateCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(activeHistory.RegionCode, row.RegionCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(activeHistory.ZoneName, row.ZoneName, StringComparison.Ordinal) ||
                !string.Equals(activeHistory.StateName, row.StateName, StringComparison.Ordinal) ||
                !string.Equals(activeHistory.RegionName, row.RegionName, StringComparison.Ordinal);

            if (hierarchyWillChange && effectiveDate < activeHistory.EffectiveFromDate)
            {
                additionalErrors.Add(new OrganisationImportValidationError(
                    RowNumber: row.RowNumber,
                    FieldName: nameof(OrganisationImportRow.BranchCode),
                    RawValue: row.BranchCode,
                    ErrorCode: OrganisationImportErrorCode.InvalidEffectiveDateTransition,
                    Message: $"Cannot apply hierarchy change for branch '{row.BranchCode}' with EffectiveFromDate ({effectiveDate:yyyy-MM-dd}) earlier than its current active mapping start date ({activeHistory.EffectiveFromDate:yyyy-MM-dd})."));
                invalidRowNumbers.Add(row.RowNumber);
            }
        }

        if (additionalErrors.Count == 0)
        {
            return batchReport;
        }

        return new OrganisationImportValidationReport(
            TotalRowsRead: batchReport.TotalRowsRead,
            ValidRowCount: batchReport.ValidRowCount - invalidRowNumbers.Count,
            InvalidRowCount: invalidRowNumbers.Count,
            IsValid: false,
            DistinctZonesCount: batchReport.DistinctZonesCount,
            DistinctStatesCount: batchReport.DistinctStatesCount,
            DistinctRegionsCount: batchReport.DistinctRegionsCount,
            DistinctBranchesCount: batchReport.DistinctBranchesCount,
            Errors: additionalErrors,
            ValidatedRows: Array.Empty<ValidatedOrganisationHierarchyRow>());
    }

    private async Task<OrganisationImportExecutionResult> ExecuteImportCoreAsync(
        OrganisationImportValidationReport validationReport,
        DateOnly effectiveDate,
        string? importedBy,
        string? changeReason,
        CancellationToken cancellationToken)
    {
        var nowUtc = _businessClock.UtcNow;
        var actor = string.IsNullOrWhiteSpace(importedBy) ? "SystemImport" : importedBy.Trim();
        var validatedRows = validationReport.ValidatedRows;

        int zonesCreated = 0, zonesUpdated = 0;
        int statesCreated = 0, statesUpdated = 0;
        int regionsCreated = 0, regionsUpdated = 0;
        int branchesCreated = 0, branchesUpdated = 0;
        int mappingHistoriesCreated = 0;

        // 1. Upsert Zones
        var zoneCodes = validatedRows.Select(r => r.ZoneCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existingZones = await _dbContext.Zones
            .Where(z => zoneCodes.Contains(z.ZoneCode))
            .ToDictionaryAsync(z => z.ZoneCode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        foreach (var zoneGroup in validatedRows.GroupBy(r => r.ZoneCode, StringComparer.OrdinalIgnoreCase))
        {
            var sample = zoneGroup.First();
            if (!existingZones.TryGetValue(sample.ZoneCode, out var zone))
            {
                zone = ZoneMaster.Create(sample.ZoneCode, sample.ZoneName, sample.IsZoneActive, actor, nowUtc);
                _dbContext.Zones.Add(zone);
                existingZones[zone.ZoneCode] = zone;
                zonesCreated++;
            }
            else if (!string.Equals(zone.ZoneName, sample.ZoneName, StringComparison.Ordinal) ||
                     zone.IsActive != sample.IsZoneActive)
            {
                zone.UpdateDetails(sample.ZoneCode, sample.ZoneName, sample.IsZoneActive, actor, nowUtc);
                zonesUpdated++;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // 2. Upsert States
        var stateCodes = validatedRows.Select(r => r.StateCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existingStates = await _dbContext.States
            .Include(s => s.Zone)
            .Where(s => stateCodes.Contains(s.StateCode))
            .ToDictionaryAsync(s => s.StateCode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        foreach (var stateGroup in validatedRows.GroupBy(r => r.StateCode, StringComparer.OrdinalIgnoreCase))
        {
            var sample = stateGroup.First();
            var parentZone = existingZones[sample.ZoneCode];

            if (!existingStates.TryGetValue(sample.StateCode, out var state))
            {
                state = StateMaster.Create(parentZone, sample.StateCode, sample.StateName, sample.IsStateActive, actor, nowUtc);
                _dbContext.States.Add(state);
                existingStates[state.StateCode] = state;
                statesCreated++;
            }
            else if (state.ZoneId != parentZone.ZoneId ||
                     !string.Equals(state.StateName, sample.StateName, StringComparison.Ordinal) ||
                     state.IsActive != sample.IsStateActive)
            {
                state.UpdateDetails(parentZone, sample.StateCode, sample.StateName, sample.IsStateActive, actor, nowUtc);
                statesUpdated++;
            }
            else
            {
                state.Zone = parentZone;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // 3. Upsert Regions
        var regionCodes = validatedRows.Select(r => r.RegionCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existingRegions = await _dbContext.Regions
            .Include(r => r.State)
            .Where(r => regionCodes.Contains(r.RegionCode))
            .ToDictionaryAsync(r => r.RegionCode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        foreach (var regionGroup in validatedRows.GroupBy(r => r.RegionCode, StringComparer.OrdinalIgnoreCase))
        {
            var sample = regionGroup.First();
            var parentState = existingStates[sample.StateCode];

            if (!existingRegions.TryGetValue(sample.RegionCode, out var region))
            {
                region = RegionMaster.Create(parentState, sample.RegionCode, sample.RegionName, sample.IsRegionActive, actor, nowUtc);
                _dbContext.Regions.Add(region);
                existingRegions[region.RegionCode] = region;
                regionsCreated++;
            }
            else if (region.StateId != parentState.StateId ||
                     !string.Equals(region.RegionName, sample.RegionName, StringComparison.Ordinal) ||
                     region.IsActive != sample.IsRegionActive)
            {
                region.UpdateDetails(parentState, sample.RegionCode, sample.RegionName, sample.IsRegionActive, actor, nowUtc);
                regionsUpdated++;
            }
            else
            {
                region.State = parentState;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // 4. Upsert Branches & Effective-Dated OrganisationMappingHistory
        var branchCodes = validatedRows.Select(r => r.BranchCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existingBranches = await _dbContext.Branches
            .Include(b => b.MappingHistories)
            .Where(b => branchCodes.Contains(b.BranchCode))
            .ToDictionaryAsync(b => b.BranchCode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var deferredNewHistoriesForExistingBranches = new List<OrganisationMappingHistory>();

        foreach (var row in validatedRows)
        {
            var zone = existingZones[row.ZoneCode];
            var state = existingStates[row.StateCode];
            var region = existingRegions[row.RegionCode];

            if (!existingBranches.TryGetValue(row.BranchCode, out var branch))
            {
                branch = BranchMaster.CreateWithInitialHistory(
                    zone: zone,
                    state: state,
                    region: region,
                    branchCode: row.BranchCode,
                    branchName: row.BranchName,
                    effectiveFromDate: effectiveDate,
                    isActive: row.IsBranchActive,
                    createdBy: actor,
                    createdAtUtc: nowUtc,
                    changeReason: changeReason ?? "Initial hierarchy import");

                _dbContext.Branches.Add(branch);
                existingBranches[branch.BranchCode] = branch;
                branchesCreated++;
                mappingHistoriesCreated++;
            }
            else
            {
                int historyCountBefore = branch.MappingHistories.Count;
                bool branchDetailsChanged =
                    branch.ZoneId != zone.ZoneId ||
                    branch.StateId != state.StateId ||
                    branch.RegionId != region.RegionId ||
                    !string.Equals(branch.BranchName, row.BranchName, StringComparison.Ordinal) ||
                    branch.IsActive != row.IsBranchActive;

                bool hierarchySnapshotChanged = branch.ApplyHierarchyAndDetailsUpdate(
                    zone: zone,
                    state: state,
                    region: region,
                    branchName: row.BranchName,
                    isActive: row.IsBranchActive,
                    effectiveDate: effectiveDate,
                    updatedBy: actor,
                    updatedAtUtc: nowUtc,
                    changeReason: changeReason ?? "Hierarchy import update");

                if (branchDetailsChanged || hierarchySnapshotChanged)
                {
                    branchesUpdated++;
                }

                if (branch.MappingHistories.Count > historyCountBefore)
                {
                    mappingHistoriesCreated++;

                    // Temporarily hold the newly added open history so EF Core flushes the closed history UPDATE first,
                    // avoiding any transient conflict on UX_OrganisationMappingHistory_ActivePerBranch (WHERE EffectiveToDate IS NULL).
                    var newlyAddedHistory = branch.MappingHistories.Last(h => h.MappingHistoryId == 0 && h.EffectiveToDate == null);
                    branch.MappingHistories.Remove(newlyAddedHistory);
                    _dbContext.Entry(newlyAddedHistory).State = EntityState.Detached;
                    deferredNewHistoriesForExistingBranches.Add(newlyAddedHistory);
                }
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (deferredNewHistoriesForExistingBranches.Count > 0)
        {
            _dbContext.OrganisationMappingHistories.AddRange(deferredNewHistoriesForExistingBranches);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return new OrganisationImportExecutionResult(
            ValidateOnly: false,
            WasPersisted: true,
            EffectiveFromDate: effectiveDate,
            ValidationReport: validationReport,
            ZonesCreated: zonesCreated,
            ZonesUpdated: zonesUpdated,
            StatesCreated: statesCreated,
            StatesUpdated: statesUpdated,
            RegionsCreated: regionsCreated,
            RegionsUpdated: regionsUpdated,
            BranchesCreated: branchesCreated,
            BranchesUpdated: branchesUpdated,
            MappingHistoriesCreated: mappingHistoriesCreated);
    }

    public async Task<IReadOnlyList<BranchSummaryDto>> GetBranchesAsync(
        BranchFilterQuery? filter = null,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Branches
            .AsNoTracking()
            .Include(b => b.Zone)
            .Include(b => b.State)
            .Include(b => b.Region)
            .AsQueryable();

        if (filter is not null)
        {
            if (filter.IsActive.HasValue)
            {
                query = query.Where(b => b.IsActive == filter.IsActive.Value);
            }

            if (!string.IsNullOrWhiteSpace(filter.ZoneCode))
            {
                var zoneCode = filter.ZoneCode.Trim().ToUpperInvariant();
                query = query.Where(b => b.Zone.ZoneCode == zoneCode);
            }

            if (!string.IsNullOrWhiteSpace(filter.StateCode))
            {
                var stateCode = filter.StateCode.Trim().ToUpperInvariant();
                query = query.Where(b => b.State.StateCode == stateCode);
            }

            if (!string.IsNullOrWhiteSpace(filter.RegionCode))
            {
                var regionCode = filter.RegionCode.Trim().ToUpperInvariant();
                query = query.Where(b => b.Region.RegionCode == regionCode);
            }

            if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
            {
                var search = filter.SearchTerm.Trim();
                query = query.Where(b =>
                    b.BranchCode.Contains(search) ||
                    b.BranchName.Contains(search));
            }
        }

        var branches = await query
            .OrderBy(b => b.Zone.ZoneCode)
            .ThenBy(b => b.State.StateCode)
            .ThenBy(b => b.Region.RegionCode)
            .ThenBy(b => b.BranchCode)
            .ToListAsync(cancellationToken);

        return branches
            .Select(b => new BranchSummaryDto(
                BranchId: b.BranchId,
                BranchCode: b.BranchCode,
                BranchName: b.BranchName,
                IsActive: b.IsActive,
                RegionId: b.RegionId,
                RegionCode: b.Region.RegionCode,
                RegionName: b.Region.RegionName,
                IsRegionActive: b.Region.IsActive,
                StateId: b.StateId,
                StateCode: b.State.StateCode,
                StateName: b.State.StateName,
                IsStateActive: b.State.IsActive,
                ZoneId: b.ZoneId,
                ZoneCode: b.Zone.ZoneCode,
                ZoneName: b.Zone.ZoneName,
                IsZoneActive: b.Zone.IsActive,
                CanSubmitActivityUpdates: b.CanSubmitActivityUpdate(out _),
                CreatedAtUtc: b.CreatedAtUtc,
                UpdatedAtUtc: b.UpdatedAtUtc))
            .ToList();
    }

    public async Task<IReadOnlyList<ZoneHierarchyNodeDto>> GetHierarchyTreeAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var zonesQuery = _dbContext.Zones
            .AsNoTracking()
            .Include(z => z.States)
                .ThenInclude(s => s.Regions)
                    .ThenInclude(r => r.Branches)
            .AsQueryable();

        if (!includeInactive)
        {
            zonesQuery = zonesQuery.Where(z => z.IsActive);
        }

        var zones = await zonesQuery
            .OrderBy(z => z.ZoneCode)
            .ToListAsync(cancellationToken);

        return zones
            .Select(z => new ZoneHierarchyNodeDto(
                ZoneId: z.ZoneId,
                ZoneCode: z.ZoneCode,
                ZoneName: z.ZoneName,
                IsActive: z.IsActive,
                States: z.States
                    .Where(s => includeInactive || s.IsActive)
                    .OrderBy(s => s.StateCode)
                    .Select(s => new StateHierarchyNodeDto(
                        StateId: s.StateId,
                        StateCode: s.StateCode,
                        StateName: s.StateName,
                        IsActive: s.IsActive,
                        Regions: s.Regions
                            .Where(r => includeInactive || r.IsActive)
                            .OrderBy(r => r.RegionCode)
                            .Select(r => new RegionHierarchyNodeDto(
                                RegionId: r.RegionId,
                                RegionCode: r.RegionCode,
                                RegionName: r.RegionName,
                                IsActive: r.IsActive,
                                Branches: r.Branches
                                    .Where(b => includeInactive || b.IsActive)
                                    .OrderBy(b => b.BranchCode)
                                    .Select(b => new BranchHierarchyNodeDto(
                                        BranchId: b.BranchId,
                                        BranchCode: b.BranchCode,
                                        BranchName: b.BranchName,
                                        IsActive: b.IsActive))
                                    .ToList()))
                            .ToList()))
                    .ToList()))
            .ToList();
    }

    public async Task<BranchMappingHistoryResultDto?> GetBranchMappingHistoryAsync(
        string branchCode,
        DateOnly? asOfReportingDate = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(branchCode))
        {
            return null;
        }

        string normalizedCode;
        try
        {
            normalizedCode = OrganisationCodeNormalizer.NormalizeCode(branchCode, nameof(branchCode));
        }
        catch (ArgumentException)
        {
            return null;
        }

        var branch = await _dbContext.Branches
            .AsNoTracking()
            .Include(b => b.MappingHistories)
            .SingleOrDefaultAsync(b => b.BranchCode == normalizedCode, cancellationToken);

        if (branch is null)
        {
            return null;
        }

        var effectiveQueryDate = asOfReportingDate ?? _businessClock.GetBusinessDate("Asia/Kolkata");
        var resolvedEntity = branch.ResolveMappingAsOf(effectiveQueryDate);

        var historyEntries = branch.MappingHistories
            .OrderByDescending(h => h.EffectiveFromDate)
            .ThenByDescending(h => h.MappingHistoryId)
            .Select(MapHistoryEntry)
            .ToList();

        return new BranchMappingHistoryResultDto(
            BranchId: branch.BranchId,
            BranchCode: branch.BranchCode,
            BranchName: branch.BranchName,
            IsBranchActive: branch.IsActive,
            AsOfReportingDate: effectiveQueryDate,
            ResolvedMappingAsOfDate: resolvedEntity is null ? null : MapHistoryEntry(resolvedEntity),
            HistoryEntries: historyEntries);
    }

    private static BranchMappingHistoryEntryDto MapHistoryEntry(OrganisationMappingHistory h) =>
        new(
            MappingHistoryId: h.MappingHistoryId,
            BranchId: h.BranchId,
            BranchCode: h.BranchCode,
            ZoneId: h.ZoneId,
            ZoneCode: h.ZoneCode,
            ZoneName: h.ZoneName,
            StateId: h.StateId,
            StateCode: h.StateCode,
            StateName: h.StateName,
            RegionId: h.RegionId,
            RegionCode: h.RegionCode,
            RegionName: h.RegionName,
            EffectiveFromDate: h.EffectiveFromDate,
            EffectiveToDate: h.EffectiveToDate,
            ChangeReason: h.ChangeReason,
            RecordedAtUtc: h.RecordedAtUtc,
            RecordedBy: h.RecordedBy,
            IsCurrentActiveMapping: h.EffectiveToDate == null);
}
