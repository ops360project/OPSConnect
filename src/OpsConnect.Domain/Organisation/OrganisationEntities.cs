using OpsConnect.Domain.Common;

namespace OpsConnect.Domain.Organisation;

public static class OrganisationCodeNormalizer
{
    public const int MaxCodeLength = 50;

    public static string NormalizeCode(string? rawCode, string paramName)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            throw new ArgumentException($"{paramName} is required and cannot be empty or whitespace.", paramName);
        }

        var normalized = rawCode.Trim().ToUpperInvariant();
        if (normalized.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException($"{paramName} ('{rawCode}') cannot contain internal whitespace.", paramName);
        }

        return normalized;
    }

    public static string NormalizeAndValidateBranchCode(string? rawBranchCode, string paramName = "branchCode")
    {
        var normalized = NormalizeCode(rawBranchCode, paramName);

        if (normalized.Length > MaxCodeLength)
        {
            throw new ArgumentException(
                $"{paramName} ('{rawBranchCode}') exceeds the maximum length of {MaxCodeLength} characters.",
                paramName);
        }

        if (normalized[0] is ':' or '-' or '_' ||
            normalized[^1] is ':' or '-' or '_' ||
            normalized.Contains("::", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{paramName} ('{rawBranchCode}') has an invalid branch code format.",
                paramName);
        }

        foreach (var ch in normalized)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != ':' && ch != '-' && ch != '_')
            {
                throw new ArgumentException(
                    $"{paramName} ('{rawBranchCode}') contains invalid character '{ch}'.",
                    paramName);
            }
        }

        return normalized;
    }

    public static string NormalizeName(string? rawName, string paramName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            throw new ArgumentException($"{paramName} is required and cannot be empty or whitespace.", paramName);
        }

        return rawName.Trim();
    }
}

public class ZoneMaster : ActivatableAuditableEntity
{
    public int ZoneId { get; set; }
    public string ZoneCode { get; set; } = string.Empty;
    public string ZoneName { get; set; } = string.Empty;

    public ICollection<StateMaster> States { get; set; } = new List<StateMaster>();
    public ICollection<BranchMaster> Branches { get; set; } = new List<BranchMaster>();

    public static ZoneMaster Create(
        string zoneCode,
        string zoneName,
        bool isActive = true,
        string? createdBy = null,
        DateTime? createdAtUtc = null)
    {
        return new ZoneMaster
        {
            ZoneCode = OrganisationCodeNormalizer.NormalizeCode(zoneCode, nameof(zoneCode)),
            ZoneName = OrganisationCodeNormalizer.NormalizeName(zoneName, nameof(zoneName)),
            IsActive = isActive,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim(),
            CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow
        };
    }

    public void UpdateDetails(
        string zoneCode,
        string zoneName,
        bool isActive,
        string? updatedBy = null,
        DateTime? updatedAtUtc = null)
    {
        ZoneCode = OrganisationCodeNormalizer.NormalizeCode(zoneCode, nameof(zoneCode));
        ZoneName = OrganisationCodeNormalizer.NormalizeName(zoneName, nameof(zoneName));
        IsActive = isActive;
        UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy.Trim();
        UpdatedAtUtc = updatedAtUtc ?? DateTime.UtcNow;
    }
}

public class StateMaster : ActivatableAuditableEntity
{
    public int StateId { get; set; }
    public int ZoneId { get; set; }
    public string StateCode { get; set; } = string.Empty;
    public string StateName { get; set; } = string.Empty;

    public ZoneMaster Zone { get; set; } = null!;
    public ICollection<RegionMaster> Regions { get; set; } = new List<RegionMaster>();
    public ICollection<BranchMaster> Branches { get; set; } = new List<BranchMaster>();

    public static StateMaster Create(
        ZoneMaster zone,
        string stateCode,
        string stateName,
        bool isActive = true,
        string? createdBy = null,
        DateTime? createdAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (isActive && !zone.IsActive)
        {
            throw new InvalidOperationException(
                $"Cannot create or activate State '{stateCode}' under inactive Zone '{zone.ZoneCode}'.");
        }

        return new StateMaster
        {
            ZoneId = zone.ZoneId,
            Zone = zone,
            StateCode = OrganisationCodeNormalizer.NormalizeCode(stateCode, nameof(stateCode)),
            StateName = OrganisationCodeNormalizer.NormalizeName(stateName, nameof(stateName)),
            IsActive = isActive,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim(),
            CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow
        };
    }

    public void UpdateDetails(
        ZoneMaster zone,
        string stateCode,
        string stateName,
        bool isActive,
        string? updatedBy = null,
        DateTime? updatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (isActive && !zone.IsActive)
        {
            throw new InvalidOperationException(
                $"Cannot activate or assign active State '{stateCode}' under inactive Zone '{zone.ZoneCode}'.");
        }

        ZoneId = zone.ZoneId;
        Zone = zone;
        StateCode = OrganisationCodeNormalizer.NormalizeCode(stateCode, nameof(stateCode));
        StateName = OrganisationCodeNormalizer.NormalizeName(stateName, nameof(stateName));
        IsActive = isActive;
        UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy.Trim();
        UpdatedAtUtc = updatedAtUtc ?? DateTime.UtcNow;
    }
}

public class RegionMaster : ActivatableAuditableEntity
{
    public int RegionId { get; set; }
    public int StateId { get; set; }
    public string RegionCode { get; set; } = string.Empty;
    public string RegionName { get; set; } = string.Empty;

    public StateMaster State { get; set; } = null!;
    public ICollection<BranchMaster> Branches { get; set; } = new List<BranchMaster>();

    public static RegionMaster Create(
        StateMaster state,
        string regionCode,
        string regionName,
        bool isActive = true,
        string? createdBy = null,
        DateTime? createdAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (isActive && !state.IsActive)
        {
            throw new InvalidOperationException(
                $"Cannot create or activate Region '{regionCode}' under inactive State '{state.StateCode}'.");
        }

        if (isActive && state.Zone is not null && !state.Zone.IsActive)
        {
            throw new InvalidOperationException(
                $"Cannot create or activate Region '{regionCode}' because parent Zone '{state.Zone.ZoneCode}' is inactive.");
        }

        return new RegionMaster
        {
            StateId = state.StateId,
            State = state,
            RegionCode = OrganisationCodeNormalizer.NormalizeCode(regionCode, nameof(regionCode)),
            RegionName = OrganisationCodeNormalizer.NormalizeName(regionName, nameof(regionName)),
            IsActive = isActive,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim(),
            CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow
        };
    }

    public void UpdateDetails(
        StateMaster state,
        string regionCode,
        string regionName,
        bool isActive,
        string? updatedBy = null,
        DateTime? updatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (isActive && !state.IsActive)
        {
            throw new InvalidOperationException(
                $"Cannot activate or assign active Region '{regionCode}' under inactive State '{state.StateCode}'.");
        }

        if (isActive && state.Zone is not null && !state.Zone.IsActive)
        {
            throw new InvalidOperationException(
                $"Cannot activate or assign active Region '{regionCode}' because parent Zone '{state.Zone.ZoneCode}' is inactive.");
        }

        StateId = state.StateId;
        State = state;
        RegionCode = OrganisationCodeNormalizer.NormalizeCode(regionCode, nameof(regionCode));
        RegionName = OrganisationCodeNormalizer.NormalizeName(regionName, nameof(regionName));
        IsActive = isActive;
        UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy.Trim();
        UpdatedAtUtc = updatedAtUtc ?? DateTime.UtcNow;
    }
}

public class BranchMaster : ActivatableAuditableEntity
{
    public int BranchId { get; set; }
    public int ZoneId { get; set; }
    public int StateId { get; set; }
    public int RegionId { get; set; }

    /// <summary>
    /// Company branch identifier format, e.g. "36:01".
    /// </summary>
    public string BranchCode { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public ZoneMaster Zone { get; set; } = null!;
    public StateMaster State { get; set; } = null!;
    public RegionMaster Region { get; set; } = null!;
    public ICollection<OrganisationMappingHistory> MappingHistories { get; set; } = new List<OrganisationMappingHistory>();

    /// <summary>
    /// Enforces four-tier transitive hierarchy consistency (Zone -> State -> Region -> Branch)
    /// and active-parent lifecycle rules.
    /// </summary>
    public static void ValidateTransitiveHierarchy(
        ZoneMaster zone,
        StateMaster state,
        RegionMaster region,
        bool isBranchActive)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(region);

        bool stateBelongsToZone =
            (zone.ZoneId > 0 && state.ZoneId > 0 && state.ZoneId == zone.ZoneId) ||
            ReferenceEquals(state.Zone, zone) ||
            (state.Zone is not null &&
             !string.IsNullOrWhiteSpace(state.Zone.ZoneCode) &&
             string.Equals(state.Zone.ZoneCode, zone.ZoneCode, StringComparison.OrdinalIgnoreCase));

        if (!stateBelongsToZone)
        {
            throw new InvalidOperationException(
                $"Hierarchy mismatch: State '{state.StateCode}' does not belong to Zone '{zone.ZoneCode}'.");
        }

        bool regionBelongsToState =
            (state.StateId > 0 && region.StateId > 0 && region.StateId == state.StateId) ||
            ReferenceEquals(region.State, state) ||
            (region.State is not null &&
             !string.IsNullOrWhiteSpace(region.State.StateCode) &&
             string.Equals(region.State.StateCode, state.StateCode, StringComparison.OrdinalIgnoreCase));

        if (!regionBelongsToState)
        {
            throw new InvalidOperationException(
                $"Hierarchy mismatch: Region '{region.RegionCode}' does not belong to State '{state.StateCode}'.");
        }

        if (isBranchActive)
        {
            if (!zone.IsActive)
            {
                throw new InvalidOperationException(
                    $"Active branch cannot be mapped under inactive Zone '{zone.ZoneCode}'.");
            }

            if (!state.IsActive)
            {
                throw new InvalidOperationException(
                    $"Active branch cannot be mapped under inactive State '{state.StateCode}'.");
            }

            if (!region.IsActive)
            {
                throw new InvalidOperationException(
                    $"Active branch cannot be mapped under inactive Region '{region.RegionCode}'.");
            }
        }
    }

    public static BranchMaster CreateWithInitialHistory(
        ZoneMaster zone,
        StateMaster state,
        RegionMaster region,
        string branchCode,
        string branchName,
        DateOnly effectiveFromDate,
        bool isActive = true,
        string? createdBy = null,
        DateTime? createdAtUtc = null,
        string? changeReason = null)
    {
        ValidateTransitiveHierarchy(zone, state, region, isActive);

        var normalizedBranchCode = OrganisationCodeNormalizer.NormalizeCode(branchCode, nameof(branchCode));
        var normalizedBranchName = OrganisationCodeNormalizer.NormalizeName(branchName, nameof(branchName));
        var timestampUtc = createdAtUtc ?? DateTime.UtcNow;
        var actor = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim();

        var branch = new BranchMaster
        {
            ZoneId = zone.ZoneId,
            Zone = zone,
            StateId = state.StateId,
            State = state,
            RegionId = region.RegionId,
            Region = region,
            BranchCode = normalizedBranchCode,
            BranchName = normalizedBranchName,
            IsActive = isActive,
            CreatedBy = actor,
            CreatedAtUtc = timestampUtc
        };

        var initialHistory = OrganisationMappingHistory.CreateOpenMapping(
            branch: branch,
            zone: zone,
            state: state,
            region: region,
            effectiveFromDate: effectiveFromDate,
            changeReason: string.IsNullOrWhiteSpace(changeReason) ? "Initial branch hierarchy registration" : changeReason.Trim(),
            recordedBy: actor,
            recordedAtUtc: timestampUtc);

        branch.MappingHistories.Add(initialHistory);
        return branch;
    }

    /// <summary>
    /// Updates branch details and hierarchy assignment. If the hierarchy mapping changes,
    /// closes the previous open OrganisationMappingHistory record as of (effectiveDate - 1 day)
    /// and creates a new open effective-dated mapping record so historical reports never change.
    /// </summary>
    public bool ApplyHierarchyAndDetailsUpdate(
        ZoneMaster zone,
        StateMaster state,
        RegionMaster region,
        string branchName,
        bool isActive,
        DateOnly effectiveDate,
        string? updatedBy = null,
        DateTime? updatedAtUtc = null,
        string? changeReason = null)
    {
        ValidateTransitiveHierarchy(zone, state, region, isActive);

        var normalizedBranchName = OrganisationCodeNormalizer.NormalizeName(branchName, nameof(branchName));
        var timestampUtc = updatedAtUtc ?? DateTime.UtcNow;
        var actor = string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy.Trim();

        BranchName = normalizedBranchName;
        IsActive = isActive;
        UpdatedBy = actor;
        UpdatedAtUtc = timestampUtc;

        var activeHistory = MappingHistories.SingleOrDefault(h => h.EffectiveToDate == null);

        bool hierarchyChanged =
            activeHistory is null ||
            !string.Equals(activeHistory.ZoneCode, zone.ZoneCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(activeHistory.StateCode, state.StateCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(activeHistory.RegionCode, region.RegionCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(activeHistory.ZoneName, zone.ZoneName, StringComparison.Ordinal) ||
            !string.Equals(activeHistory.StateName, state.StateName, StringComparison.Ordinal) ||
            !string.Equals(activeHistory.RegionName, region.RegionName, StringComparison.Ordinal);

        ZoneId = zone.ZoneId;
        Zone = zone;
        StateId = state.StateId;
        State = state;
        RegionId = region.RegionId;
        Region = region;

        if (!hierarchyChanged)
        {
            return false;
        }

        if (activeHistory is not null)
        {
            if (effectiveDate < activeHistory.EffectiveFromDate)
            {
                throw new InvalidOperationException(
                    $"Cannot apply hierarchy change with EffectiveFromDate ({effectiveDate:yyyy-MM-dd}) earlier than current active mapping start date ({activeHistory.EffectiveFromDate:yyyy-MM-dd}).");
            }

            if (effectiveDate == activeHistory.EffectiveFromDate)
            {
                // Same-day correction updates the current open mapping snapshot without creating overlapping rows
                activeHistory.UpdateSameDaySnapshot(
                    zone: zone,
                    state: state,
                    region: region,
                    changeReason: changeReason ?? "Same-day hierarchy mapping update",
                    recordedBy: actor,
                    recordedAtUtc: timestampUtc);
                return true;
            }

            activeHistory.CloseMapping(effectiveDate.AddDays(-1));
        }

        var newHistory = OrganisationMappingHistory.CreateOpenMapping(
            branch: this,
            zone: zone,
            state: state,
            region: region,
            effectiveFromDate: effectiveDate,
            changeReason: string.IsNullOrWhiteSpace(changeReason) ? "Hierarchy mapping updated" : changeReason.Trim(),
            recordedBy: actor,
            recordedAtUtc: timestampUtc);

        MappingHistories.Add(newHistory);
        return true;
    }

    public bool CanSubmitActivityUpdate(out string? rejectionReason)
    {
        if (!IsActive)
        {
            rejectionReason = $"Branch '{BranchCode}' is inactive and cannot submit activity updates.";
            return false;
        }

        if (Region is { IsActive: false })
        {
            rejectionReason = $"Parent Region '{Region.RegionCode}' for Branch '{BranchCode}' is inactive.";
            return false;
        }

        if (State is { IsActive: false })
        {
            rejectionReason = $"Parent State '{State.StateCode}' for Branch '{BranchCode}' is inactive.";
            return false;
        }

        if (Zone is { IsActive: false })
        {
            rejectionReason = $"Parent Zone '{Zone.ZoneCode}' for Branch '{BranchCode}' is inactive.";
            return false;
        }

        rejectionReason = null;
        return true;
    }

    public OrganisationMappingHistory? ResolveMappingAsOf(DateOnly reportingDate)
    {
        return MappingHistories
            .Where(h => h.EffectiveFromDate <= reportingDate &&
                        (h.EffectiveToDate == null || h.EffectiveToDate >= reportingDate))
            .OrderByDescending(h => h.EffectiveFromDate)
            .FirstOrDefault();
    }
}

/// <summary>
/// Effective-dated hierarchy mapping history ensuring historical reports remain consistent
/// even if a branch is later moved across regions, states, or zones.
/// </summary>
public class OrganisationMappingHistory
{
    public long MappingHistoryId { get; set; }
    public int BranchId { get; set; }
    public string BranchCode { get; set; } = string.Empty;

    public int ZoneId { get; set; }
    public string ZoneCode { get; set; } = string.Empty;
    public string ZoneName { get; set; } = string.Empty;

    public int StateId { get; set; }
    public string StateCode { get; set; } = string.Empty;
    public string StateName { get; set; } = string.Empty;

    public int RegionId { get; set; }
    public string RegionCode { get; set; } = string.Empty;
    public string RegionName { get; set; } = string.Empty;

    public DateOnly EffectiveFromDate { get; set; }
    public DateOnly? EffectiveToDate { get; set; }
    public string? ChangeReason { get; set; }
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
    public string? RecordedBy { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public BranchMaster Branch { get; set; } = null!;

    public static OrganisationMappingHistory CreateOpenMapping(
        BranchMaster branch,
        ZoneMaster zone,
        StateMaster state,
        RegionMaster region,
        DateOnly effectiveFromDate,
        string? changeReason,
        string? recordedBy,
        DateTime recordedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(region);

        return new OrganisationMappingHistory
        {
            BranchId = branch.BranchId,
            Branch = branch,
            BranchCode = branch.BranchCode,
            ZoneId = zone.ZoneId,
            ZoneCode = zone.ZoneCode,
            ZoneName = zone.ZoneName,
            StateId = state.StateId,
            StateCode = state.StateCode,
            StateName = state.StateName,
            RegionId = region.RegionId,
            RegionCode = region.RegionCode,
            RegionName = region.RegionName,
            EffectiveFromDate = effectiveFromDate,
            EffectiveToDate = null,
            ChangeReason = string.IsNullOrWhiteSpace(changeReason) ? null : changeReason.Trim(),
            RecordedBy = string.IsNullOrWhiteSpace(recordedBy) ? null : recordedBy.Trim(),
            RecordedAtUtc = recordedAtUtc
        };
    }

    public void CloseMapping(DateOnly effectiveToDate)
    {
        if (effectiveToDate < EffectiveFromDate)
        {
            throw new InvalidOperationException(
                $"EffectiveToDate ({effectiveToDate:yyyy-MM-dd}) cannot be earlier than EffectiveFromDate ({EffectiveFromDate:yyyy-MM-dd}).");
        }

        EffectiveToDate = effectiveToDate;
    }

    internal void UpdateSameDaySnapshot(
        ZoneMaster zone,
        StateMaster state,
        RegionMaster region,
        string? changeReason,
        string? recordedBy,
        DateTime recordedAtUtc)
    {
        ZoneId = zone.ZoneId;
        ZoneCode = zone.ZoneCode;
        ZoneName = zone.ZoneName;
        StateId = state.StateId;
        StateCode = state.StateCode;
        StateName = state.StateName;
        RegionId = region.RegionId;
        RegionCode = region.RegionCode;
        RegionName = region.RegionName;
        ChangeReason = string.IsNullOrWhiteSpace(changeReason) ? ChangeReason : changeReason.Trim();
        RecordedBy = string.IsNullOrWhiteSpace(recordedBy) ? RecordedBy : recordedBy.Trim();
        RecordedAtUtc = recordedAtUtc;
    }
}
