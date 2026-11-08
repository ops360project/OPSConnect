namespace OpsConnect.Application.Reporting;

/// <summary>
/// Domain-agnostic reporting aggregation service providing activity, hierarchy, and branch-level reporting summaries.
/// </summary>
public interface IActivityReportingService
{
    /// <summary>
    /// Generates top-level activity summary report with zone breakdowns for the specified reporting date and hierarchy filters.
    /// </summary>
    Task<ActivityReportSummaryDto> GetActivitySummaryReportAsync(
        ReportingFilterQuery? query = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves aggregated summaries for Zones matching the specified filters.
    /// </summary>
    Task<IReadOnlyList<ZoneReportSummaryDto>> GetZoneSummariesAsync(
        ReportingFilterQuery? query = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves aggregated summaries for States matching the specified filters.
    /// </summary>
    Task<IReadOnlyList<StateReportSummaryDto>> GetStateSummariesAsync(
        ReportingFilterQuery? query = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves aggregated summaries for Regions matching the specified filters.
    /// </summary>
    Task<IReadOnlyList<RegionReportSummaryDto>> GetRegionSummariesAsync(
        ReportingFilterQuery? query = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves detailed branch-wise reporting status records with optional filtering and pagination.
    /// </summary>
    Task<BranchReportDetailsResponseDto> GetBranchReportDetailsAsync(
        ReportingFilterQuery? query = null,
        CancellationToken cancellationToken = default);
}
