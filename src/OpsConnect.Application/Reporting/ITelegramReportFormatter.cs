namespace OpsConnect.Application.Reporting;

public enum TelegramReportDetailLevel
{
    /// <summary>
    /// Formats top-level activity summary with counts, metrics, and completion percentage only.
    /// </summary>
    SummaryOnly = 1,

    /// <summary>
    /// Formats top-level activity summary plus Zone, State, and Region breakdowns.
    /// </summary>
    HierarchySummary = 2,

    /// <summary>
    /// Formats top-level activity summary, hierarchy breakdowns, and individual branch details.
    /// </summary>
    FullBranchDetails = 3
}

public sealed record TelegramReportFormattingOptions
{
    /// <summary>
    /// Detail level of the report. Defaults to HierarchySummary.
    /// </summary>
    public TelegramReportDetailLevel DetailLevel { get; init; } = TelegramReportDetailLevel.HierarchySummary;

    /// <summary>
    /// Maximum character limit per Telegram message part. Defaults to 4000 (Telegram Bot API limit is 4096).
    /// </summary>
    public int MaxMessageLength { get; init; } = 4000;

    /// <summary>
    /// When formatting branch details, if true only branches in Pending or NotReported status are listed.
    /// </summary>
    public bool ShowBranchDetailsForPendingOnly { get; init; } = false;

    /// <summary>
    /// Target timezone ID for generation timestamp display. Defaults to "Asia/Kolkata".
    /// </summary>
    public string TimeZoneId { get; init; } = "Asia/Kolkata";

    /// <summary>
    /// Timezone abbreviation displayed in headers. Defaults to "IST".
    /// </summary>
    public string TimeZoneAbbreviation { get; init; } = "IST";

    /// <summary>
    /// Custom report title override. If null, "{ActivityName} — Daily Status" is used.
    /// </summary>
    public string? CustomTitle { get; init; }

    /// <summary>
    /// Whether to format with Telegram HTML tags (<b>, <code>). If false, clean plain text is used.
    /// </summary>
    public bool UseHtmlMarkup { get; init; } = false;
}

public sealed record FormattedTelegramReport(
    string ActivityCode,
    string ActivityName,
    DateOnly ReportingDate,
    int TotalBranches,
    decimal CompletionPercentage,
    IReadOnlyList<string> Messages)
{
    public int MessageCount => Messages.Count;
    public string PrimaryMessage => Messages.Count > 0 ? Messages[0] : string.Empty;
}

/// <summary>
/// Reusable Telegram report formatter that converts reporting DTOs into clean, mobile-readable Telegram messages.
/// Generic and reusable for RFFO, GetData, Cash, and future operational activities.
/// </summary>
public interface ITelegramReportFormatter
{
    /// <summary>
    /// Formats an activity reporting summary into an ordered collection of Telegram message parts respecting message length limits.
    /// </summary>
    FormattedTelegramReport FormatActivitySummary(
        ActivityReportSummaryDto summary,
        IReadOnlyList<BranchReportDetailDto>? branches = null,
        TelegramReportFormattingOptions? options = null);

    /// <summary>
    /// Formats branch-level report details into an ordered collection of Telegram message parts.
    /// </summary>
    FormattedTelegramReport FormatBranchDetails(
        ActivityReportSummaryDto summary,
        IReadOnlyList<BranchReportDetailDto> branches,
        TelegramReportFormattingOptions? options = null);

    /// <summary>
    /// Formats a single Zone summary block.
    /// </summary>
    string FormatZoneSummary(
        ZoneReportSummaryDto zone,
        TelegramReportFormattingOptions? options = null);

    /// <summary>
    /// Formats a single State summary block.
    /// </summary>
    string FormatStateSummary(
        StateReportSummaryDto state,
        TelegramReportFormattingOptions? options = null);

    /// <summary>
    /// Formats a single Region summary block.
    /// </summary>
    string FormatRegionSummary(
        RegionReportSummaryDto region,
        TelegramReportFormattingOptions? options = null);
}
