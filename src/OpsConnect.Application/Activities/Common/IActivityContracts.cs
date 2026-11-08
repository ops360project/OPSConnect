namespace OpsConnect.Application.Activities.Common;

/// <summary>
/// Pluggable activity-specific update handler contract keyed by ActivityCode (e.g., "RFFO", "GETDATA").
/// Keeps activity-specific validation and status processing isolated from generic message ingestion and reporting engines.
/// </summary>
public interface IActivityUpdateHandler
{
    string ActivityCode { get; }

    bool CanHandle(string? activityCode);

    Task<ActivityUpdateProcessingResult> ProcessUpdateAsync(
        ActivityUpdateSubmissionCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates standalone message parsing and dispatches validated activity updates to the appropriate
/// pluggable IActivityUpdateHandler registered for the activity code.
/// </summary>
public interface IActivityUpdateProcessor
{
    IReadOnlyCollection<string> RegisteredActivityCodes { get; }

    IActivityUpdateHandler? ResolveHandler(string? activityCode);

    Task<ActivityUpdateProcessingResult> ProcessRawMessageAsync(
        RawActivityMessageSubmissionRequest request,
        CancellationToken cancellationToken = default);

    Task<ActivityUpdateProcessingResult> ProcessParsedUpdateAsync(
        ActivityUpdateSubmissionCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Application query service for branch-wise current status (including derived NotReported status)
/// and chronological branch status audit history.
/// </summary>
public interface IActivityStatusQueryService
{
    Task<ActivityDailyStatusSnapshotDto> GetBranchStatusesAsync(
        ActivityStatusFilterQuery? query = null,
        CancellationToken cancellationToken = default);

    Task<BranchActivityHistoryResultDto?> GetBranchActivityHistoryAsync(
        string activityCode,
        string branchCode,
        DateOnly? reportingDate = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Application service for managing administrator status reopening permissions, revocation,
/// end-of-day expiration, and reopening audit history.
/// </summary>
public interface IStatusReopenPermissionService
{
    Task<StatusReopenPermissionOperationResult> GrantPermissionAsync(
        GrantStatusReopenPermissionRequest request,
        CancellationToken cancellationToken = default);

    Task<StatusReopenPermissionOperationResult> RevokePermissionAsync(
        RevokeStatusReopenPermissionRequest request,
        CancellationToken cancellationToken = default);

    Task<int> ExpireStalePermissionsAsync(
        string? activityCode = null,
        DateOnly? asOfReportingDate = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StatusReopenPermissionDto>> GetPermissionsHistoryAsync(
        StatusReopenPermissionFilterQuery? filter = null,
        CancellationToken cancellationToken = default);
}
