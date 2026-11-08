using OpsConnect.Application.Activities.Common;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Organisation;

namespace OpsConnect.Application.Reporting;

/// <summary>
/// Query filter parameters for reporting summaries and hierarchy drill-downs.
/// </summary>
public sealed record ReportingFilterQuery
{
    public string ActivityCode { get; init; } = ActivityModuleConstants.RffoActivityCode;
    public DateOnly? ReportingDate { get; init; }
    public string? ZoneCode { get; init; }
    public string? StateCode { get; init; }
    public string? RegionCode { get; init; }
    public string? BranchCode { get; init; }
    public BranchActivityStatus? Status { get; init; }
    public string? SearchTerm { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }

    public ReportingFilterQuery Normalize()
    {
        return this with
        {
            ActivityCode = string.IsNullOrWhiteSpace(ActivityCode)
                ? ActivityModuleConstants.RffoActivityCode
                : ActivityMaster.ValidateActivityCode(ActivityCode),
            ZoneCode = string.IsNullOrWhiteSpace(ZoneCode)
                ? null
                : OrganisationCodeNormalizer.NormalizeCode(ZoneCode, nameof(ZoneCode)),
            StateCode = string.IsNullOrWhiteSpace(StateCode)
                ? null
                : OrganisationCodeNormalizer.NormalizeCode(StateCode, nameof(StateCode)),
            RegionCode = string.IsNullOrWhiteSpace(RegionCode)
                ? null
                : OrganisationCodeNormalizer.NormalizeCode(RegionCode, nameof(RegionCode)),
            BranchCode = string.IsNullOrWhiteSpace(BranchCode)
                ? null
                : OrganisationCodeNormalizer.NormalizeAndValidateBranchCode(BranchCode, nameof(BranchCode)),
            SearchTerm = string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm.Trim(),
            Page = Page is > 0 ? Page : null,
            PageSize = PageSize is > 0 ? Math.Min(PageSize.Value, 500) : null
        };
    }
}

/// <summary>
/// Top-level summary for an activity on a specific reporting date.
/// </summary>
public sealed record ActivityReportSummaryDto(
    int ActivityId,
    string ActivityCode,
    string ActivityName,
    DateOnly ReportingDate,
    string TimeZoneId,
    DateTime GeneratedAtUtc,
    int TotalBranches,
    int CompletedBranches,
    int SubmittedPendingBranches,
    int NotReportedBranches,
    int PendingBranches,
    decimal CompletionPercentage,
    bool IsEmptyPopulation,
    bool IsFullyCompleted,
    int TotalZones,
    int TotalStates,
    int TotalRegions,
    IReadOnlyList<ZoneReportSummaryDto> Zones)
{
    /// <summary>
    /// Section 2 invariant: Pending count = Pending + NotReported.
    /// </summary>
    public int PendingCount => PendingBranches;
}

/// <summary>
/// Aggregated summary for a Zone.
/// </summary>
public sealed record ZoneReportSummaryDto(
    int ZoneId,
    string ZoneCode,
    string ZoneName,
    int TotalBranches,
    int CompletedBranches,
    int SubmittedPendingBranches,
    int NotReportedBranches,
    int PendingBranches,
    decimal CompletionPercentage,
    bool IsEmptyPopulation,
    bool IsFullyCompleted,
    IReadOnlyList<StateReportSummaryDto>? States = null)
{
    /// <summary>
    /// Section 2 invariant: Pending count = Pending + NotReported.
    /// </summary>
    public int PendingCount => PendingBranches;
}

/// <summary>
/// Aggregated summary for a State.
/// </summary>
public sealed record StateReportSummaryDto(
    int StateId,
    string StateCode,
    string StateName,
    int ZoneId,
    string ZoneCode,
    string ZoneName,
    int TotalBranches,
    int CompletedBranches,
    int SubmittedPendingBranches,
    int NotReportedBranches,
    int PendingBranches,
    decimal CompletionPercentage,
    bool IsEmptyPopulation,
    bool IsFullyCompleted,
    IReadOnlyList<RegionReportSummaryDto>? Regions = null)
{
    /// <summary>
    /// Section 2 invariant: Pending count = Pending + NotReported.
    /// </summary>
    public int PendingCount => PendingBranches;
}

/// <summary>
/// Aggregated summary for a Region.
/// </summary>
public sealed record RegionReportSummaryDto(
    int RegionId,
    string RegionCode,
    string RegionName,
    int StateId,
    string StateCode,
    string StateName,
    int ZoneId,
    string ZoneCode,
    string ZoneName,
    int TotalBranches,
    int CompletedBranches,
    int SubmittedPendingBranches,
    int NotReportedBranches,
    int PendingBranches,
    decimal CompletionPercentage,
    bool IsEmptyPopulation,
    bool IsFullyCompleted)
{
    /// <summary>
    /// Section 2 invariant: Pending count = Pending + NotReported.
    /// </summary>
    public int PendingCount => PendingBranches;
}

/// <summary>
/// Detailed status for a single mapped branch as of the requested reporting date.
/// </summary>
public sealed record BranchReportDetailDto(
    int BranchId,
    string BranchCode,
    string BranchName,
    int ZoneId,
    string ZoneCode,
    string ZoneName,
    int StateId,
    string StateCode,
    string StateName,
    int RegionId,
    string RegionCode,
    string RegionName,
    DateOnly ReportingDate,
    BranchActivityStatus CurrentStatus,
    bool IsDerivedNotReported,
    int ReopenedCount,
    bool HasAvailableReopenPermission,
    long? LastTelegramMessageLogId,
    long? LastUpdatedByTelegramUserId,
    string? LastUpdatedByDisplayName,
    DateTime? LastUpdatedAtUtc);

/// <summary>
/// Paginated or filtered response for branch status details.
/// </summary>
public sealed record BranchReportDetailsResponseDto(
    int ActivityId,
    string ActivityCode,
    DateOnly ReportingDate,
    int TotalMatchingBranches,
    int? Page,
    int? PageSize,
    IReadOnlyList<BranchReportDetailDto> Branches);
