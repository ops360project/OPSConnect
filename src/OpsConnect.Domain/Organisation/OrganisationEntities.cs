using OpsConnect.Domain.Common;

namespace OpsConnect.Domain.Organisation;

public class ZoneMaster : ActivatableAuditableEntity
{
    public int ZoneId { get; set; }
    public string ZoneCode { get; set; } = string.Empty;
    public string ZoneName { get; set; } = string.Empty;

    public ICollection<StateMaster> States { get; set; } = new List<StateMaster>();
    public ICollection<BranchMaster> Branches { get; set; } = new List<BranchMaster>();
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
}

public class RegionMaster : ActivatableAuditableEntity
{
    public int RegionId { get; set; }
    public int StateId { get; set; }
    public string RegionCode { get; set; } = string.Empty;
    public string RegionName { get; set; } = string.Empty;

    public StateMaster State { get; set; } = null!;
    public ICollection<BranchMaster> Branches { get; set; } = new List<BranchMaster>();
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

    public ZoneMaster Zone { get; set; } = null!;
    public StateMaster State { get; set; } = null!;
    public RegionMaster Region { get; set; } = null!;
    public ICollection<OrganisationMappingHistory> MappingHistories { get; set; } = new List<OrganisationMappingHistory>();
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

    public BranchMaster Branch { get; set; } = null!;
}
