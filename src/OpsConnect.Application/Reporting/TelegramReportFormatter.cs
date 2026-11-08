using System.Text;
using OpsConnect.Application.Common;
using OpsConnect.Domain.Activities;

namespace OpsConnect.Application.Reporting;

/// <summary>
/// Reusable Telegram report formatter that converts reporting DTOs into clean, mobile-readable Telegram messages.
/// Generic and reusable for RFFO, GetData, Cash, and future operational activities.
/// </summary>
public sealed class TelegramReportFormatter : ITelegramReportFormatter
{
    private const int MinMessageLength = 500;
    private const int TelegramApiHardLimit = 4096;
    private const int HeaderHeadroom = 80;

    public FormattedTelegramReport FormatActivitySummary(
        ActivityReportSummaryDto summary,
        IReadOnlyList<BranchReportDetailDto>? branches = null,
        TelegramReportFormattingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var opt = options ?? new TelegramReportFormattingOptions();
        int maxLen = Math.Clamp(opt.MaxMessageLength, MinMessageLength, TelegramApiHardLimit);
        int effectiveBlockLimit = maxLen - HeaderHeadroom;

        string title = !string.IsNullOrWhiteSpace(opt.CustomTitle)
            ? opt.CustomTitle.Trim()
            : $"{summary.ActivityName} — Daily Status";

        // 1. Build Header Section
        var headerBuilder = new StringBuilder();
        BuildHeaderSection(headerBuilder, summary, title, opt);
        string headerBlock = headerBuilder.ToString();

        // 2. Build Hierarchy Sections
        var contentBlocks = new List<string>();

        if (opt.DetailLevel is TelegramReportDetailLevel.HierarchySummary or TelegramReportDetailLevel.FullBranchDetails)
        {
            var branchesByRegion = branches?
                .GroupBy(b => b.RegionId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<BranchReportDetailDto>)g.ToList())
                ?? new Dictionary<int, IReadOnlyList<BranchReportDetailDto>>();

            foreach (var zone in summary.Zones)
            {
                var zoneBlock = BuildZoneBlock(zone, opt, branchesByRegion);
                contentBlocks.AddRange(zoneBlock);
            }
        }

        // 3. Assemble message chunks respecting length limits and logical boundaries
        var messageParts = AssembleMessageParts(summary, title, headerBlock, contentBlocks, maxLen, effectiveBlockLimit, opt);

        return new FormattedTelegramReport(
            ActivityCode: summary.ActivityCode,
            ActivityName: summary.ActivityName,
            ReportingDate: summary.ReportingDate,
            TotalBranches: summary.TotalBranches,
            CompletionPercentage: summary.CompletionPercentage,
            Messages: messageParts);
    }

    public FormattedTelegramReport FormatBranchDetails(
        ActivityReportSummaryDto summary,
        IReadOnlyList<BranchReportDetailDto> branches,
        TelegramReportFormattingOptions? options = null)
    {
        var opt = (options ?? new TelegramReportFormattingOptions()) with
        {
            DetailLevel = TelegramReportDetailLevel.FullBranchDetails
        };

        return FormatActivitySummary(summary, branches, opt);
    }

    public string FormatZoneSummary(
        ZoneReportSummaryDto zone,
        TelegramReportFormattingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var opt = options ?? new TelegramReportFormattingOptions();

        var sb = new StringBuilder();
        AppendZoneHeader(sb, zone, opt);
        return sb.ToString().TrimEnd();
    }

    public string FormatStateSummary(
        StateReportSummaryDto state,
        TelegramReportFormattingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var opt = options ?? new TelegramReportFormattingOptions();

        var sb = new StringBuilder();
        AppendStateHeader(sb, state, opt, indent: "");
        return sb.ToString().TrimEnd();
    }

    public string FormatRegionSummary(
        RegionReportSummaryDto region,
        TelegramReportFormattingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(region);
        var opt = options ?? new TelegramReportFormattingOptions();

        var sb = new StringBuilder();
        AppendRegionHeader(sb, region, opt, indent: "");
        return sb.ToString().TrimEnd();
    }

    private static void BuildHeaderSection(
        StringBuilder sb,
        ActivityReportSummaryDto summary,
        string title,
        TelegramReportFormattingOptions opt)
    {
        string formattedTitle = opt.UseHtmlMarkup ? $"<b>{EscapeHtml(title)}</b>" : title;
        sb.AppendLine(formattedTitle);
        sb.AppendLine($"Reporting Date: {summary.ReportingDate:dd-MM-yyyy}");

        // Timestamp in Asia/Kolkata
        var kolkataTime = ConvertToTimeZone(summary.GeneratedAtUtc, opt.TimeZoneId);
        sb.AppendLine($"Generated: {kolkataTime:dd-MM-yyyy HH:mm:ss} {opt.TimeZoneAbbreviation}");
        sb.AppendLine();

        sb.AppendLine($"Total Branches: {summary.TotalBranches}");
        sb.AppendLine($"Completed: {summary.CompletedBranches}");
        sb.AppendLine($"Submitted Pending: {summary.SubmittedPendingBranches}");
        sb.AppendLine($"Not Reported: {summary.NotReportedBranches}");
        sb.AppendLine($"Total Pending: {summary.PendingBranches}");
        sb.AppendLine($"Completion: {summary.CompletionPercentage:0.00}%");

        string statusText = summary.IsEmptyPopulation
            ? "Empty Branch Population (0 mapped active branches)"
            : summary.IsFullyCompleted
                ? "All Branches Completed"
                : "In Progress";

        string statusDisplay = opt.UseHtmlMarkup ? $"<b>{EscapeHtml(statusText)}</b>" : statusText;
        sb.AppendLine($"Status: {statusDisplay}");

        if (summary.SubmittedPendingBranches > 0 && summary.PendingReasons != null && summary.PendingReasons.Count > 0)
        {
            sb.AppendLine();
            string reasonsTitle = opt.UseHtmlMarkup ? "<b>Pending Reasons:</b>" : "Pending Reasons:";
            sb.AppendLine(reasonsTitle);
            foreach (var reason in summary.PendingReasons)
            {
                string rName = opt.UseHtmlMarkup ? EscapeHtml(reason.ReasonName) : reason.ReasonName;
                sb.AppendLine($"• {rName}: {reason.BranchCount} ({reason.PercentageOfPending:0.00}%)");
            }
        }
    }

    private static List<string> BuildZoneBlock(
        ZoneReportSummaryDto zone,
        TelegramReportFormattingOptions opt,
        Dictionary<int, IReadOnlyList<BranchReportDetailDto>> branchesByRegion)
    {
        var blocks = new List<string>();
        var sb = new StringBuilder();

        AppendZoneHeader(sb, zone, opt);

        if (zone.States is not null)
        {
            foreach (var state in zone.States)
            {
                sb.AppendLine();
                AppendStateHeader(sb, state, opt, indent: "  ");

                if (state.Regions is not null)
                {
                    foreach (var region in state.Regions)
                    {
                        sb.AppendLine();
                        AppendRegionHeader(sb, region, opt, indent: "    ");

                        if (opt.DetailLevel == TelegramReportDetailLevel.FullBranchDetails &&
                            branchesByRegion.TryGetValue(region.RegionId, out var regionBranches) &&
                            regionBranches.Count > 0)
                        {
                            AppendBranchDetails(sb, regionBranches, opt, indent: "      ");
                        }
                    }
                }
            }
        }

        blocks.Add(sb.ToString().TrimEnd());
        return blocks;
    }

    private static void AppendZoneHeader(
        StringBuilder sb,
        ZoneReportSummaryDto zone,
        TelegramReportFormattingOptions opt)
    {
        string name = opt.UseHtmlMarkup ? $"<b>{EscapeHtml(zone.ZoneName)} ({EscapeHtml(zone.ZoneCode)})</b>" : $"{zone.ZoneName} ({zone.ZoneCode})";
        sb.AppendLine($"Zone: {name}");
        sb.AppendLine($"  Completed: {zone.CompletedBranches} | Pending: {zone.SubmittedPendingBranches} | Not Reported: {zone.NotReportedBranches} | Total: {zone.TotalBranches} ({zone.CompletionPercentage:0.00}%)");
    }

    private static void AppendStateHeader(
        StringBuilder sb,
        StateReportSummaryDto state,
        TelegramReportFormattingOptions opt,
        string indent)
    {
        string name = opt.UseHtmlMarkup ? $"<b>{EscapeHtml(state.StateName)} ({EscapeHtml(state.StateCode)})</b>" : $"{state.StateName} ({state.StateCode})";
        sb.AppendLine($"{indent}State: {name}");
        sb.AppendLine($"{indent}  Completed: {state.CompletedBranches} | Pending: {state.SubmittedPendingBranches} | Not Reported: {state.NotReportedBranches} | Total: {state.TotalBranches} ({state.CompletionPercentage:0.00}%)");
    }

    private static void AppendRegionHeader(
        StringBuilder sb,
        RegionReportSummaryDto region,
        TelegramReportFormattingOptions opt,
        string indent)
    {
        string name = opt.UseHtmlMarkup ? $"<b>{EscapeHtml(region.RegionName)} ({EscapeHtml(region.RegionCode)})</b>" : $"{region.RegionName} ({region.RegionCode})";
        sb.AppendLine($"{indent}Region: {name}");
        sb.AppendLine($"{indent}  Completed: {region.CompletedBranches} | Pending: {region.SubmittedPendingBranches} | Not Reported: {region.NotReportedBranches} | Total: {region.TotalBranches} ({region.CompletionPercentage:0.00}%)");
    }

    private static void AppendBranchDetails(
        StringBuilder sb,
        IReadOnlyList<BranchReportDetailDto> branches,
        TelegramReportFormattingOptions opt,
        string indent)
    {
        var displayBranches = opt.ShowBranchDetailsForPendingOnly
            ? branches.Where(b => b.CurrentStatus != BranchActivityStatus.Completed).ToList()
            : branches;

        if (displayBranches.Count == 0)
        {
            if (opt.ShowBranchDetailsForPendingOnly && branches.Count > 0)
            {
                sb.AppendLine($"{indent}All branches completed in this region.");
            }
            return;
        }

        sb.AppendLine($"{indent}Branches:");
        foreach (var b in displayBranches)
        {
            string statusLabel = b.CurrentStatus switch
            {
                BranchActivityStatus.Completed => "Completed",
                BranchActivityStatus.Pending when b.PendingReason.HasValue =>
                    $"Pending ({b.PendingReason.Value.ToDisplayName()})",
                BranchActivityStatus.Pending => "Pending",
                _ => "Not Reported"
            };

            string code = opt.UseHtmlMarkup ? $"<code>{EscapeHtml(b.BranchCode)}</code>" : b.BranchCode;
            string bName = opt.UseHtmlMarkup ? EscapeHtml(b.BranchName) : b.BranchName;
            sb.AppendLine($"{indent}• {code} - {bName}: {statusLabel}");
        }
    }

    private static IReadOnlyList<string> AssembleMessageParts(
        ActivityReportSummaryDto summary,
        string title,
        string headerBlock,
        IReadOnlyList<string> contentBlocks,
        int maxLen,
        int effectiveBlockLimit,
        TelegramReportFormattingOptions opt)
    {
        var rawParts = new List<string>();
        var currentBuilder = new StringBuilder();
        currentBuilder.Append(headerBlock);

        foreach (var block in contentBlocks)
        {
            // If block fits in current message chunk
            if (currentBuilder.Length + block.Length + 4 <= effectiveBlockLimit)
            {
                currentBuilder.AppendLine();
                currentBuilder.AppendLine();
                currentBuilder.Append(block);
            }
            else
            {
                // If block itself is larger than effectiveBlockLimit, split by lines to never truncate branch lines
                if (block.Length > effectiveBlockLimit)
                {
                    var lines = block.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                    foreach (var line in lines)
                    {
                        if (currentBuilder.Length + line.Length + 2 > effectiveBlockLimit)
                        {
                            if (currentBuilder.Length > 0)
                            {
                                rawParts.Add(currentBuilder.ToString().TrimEnd());
                                currentBuilder.Clear();
                                currentBuilder.AppendLine("##CONTINUATION_HEADER##");
                            }
                        }
                        currentBuilder.AppendLine(line);
                    }
                }
                else
                {
                    // Flush current message chunk and start a new continuation chunk
                    if (currentBuilder.Length > 0)
                    {
                        rawParts.Add(currentBuilder.ToString().TrimEnd());
                        currentBuilder.Clear();
                    }

                    currentBuilder.AppendLine("##CONTINUATION_HEADER##");
                    currentBuilder.Append(block);
                }
            }
        }

        if (currentBuilder.Length > 0)
        {
            rawParts.Add(currentBuilder.ToString().TrimEnd());
        }

        if (rawParts.Count <= 1)
        {
            // Single part -> no "(Part 1/1)" needed
            return rawParts.Select(p => p.Replace("##CONTINUATION_HEADER##\r\n", "").Replace("##CONTINUATION_HEADER##\n", "")).ToList();
        }

        int totalParts = rawParts.Count;
        var finalParts = new List<string>(totalParts);

        for (int i = 0; i < totalParts; i++)
        {
            int partNumber = i + 1;
            string partHeader = opt.UseHtmlMarkup
                ? $"<b>{EscapeHtml(title)} (Part {partNumber}/{totalParts})</b>\nReporting Date: {summary.ReportingDate:dd-MM-yyyy}\n"
                : $"{title} (Part {partNumber}/{totalParts})\nReporting Date: {summary.ReportingDate:dd-MM-yyyy}\n";

            string content = rawParts[i];

            if (i == 0)
            {
                // First part: replace title with Part 1 of N title
                string originalFirstLine = opt.UseHtmlMarkup ? $"<b>{EscapeHtml(title)}</b>" : title;
                string newFirstLine = opt.UseHtmlMarkup
                    ? $"<b>{EscapeHtml(title)} (Part 1/{totalParts})</b>"
                    : $"{title} (Part 1/{totalParts})";

                if (content.StartsWith(originalFirstLine, StringComparison.Ordinal))
                {
                    content = newFirstLine + content[originalFirstLine.Length..];
                }
            }
            else
            {
                // Continuation part: replace placeholder with continuation header
                content = content.Replace("##CONTINUATION_HEADER##", partHeader.TrimEnd());
            }

            finalParts.Add(content.TrimEnd());
        }

        return finalParts;
    }

    private static DateTime ConvertToTimeZone(DateTime utcDateTime, string timeZoneId)
    {
        try
        {
            var tz = SystemBusinessClock.ResolveTimeZone(timeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(
                utcDateTime.Kind == DateTimeKind.Utc ? utcDateTime : DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc),
                tz);
        }
        catch (Exception)
        {
            return utcDateTime;
        }
    }

    public static string EscapeHtml(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    public static string EscapeMarkdownV2(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length * 2);
        foreach (var ch in text)
        {
            if (ch is '_' or '*' or '[' or ']' or '(' or ')' or '~' or '`' or '>' or '#' or '+' or '-' or '=' or '|' or '{' or '}' or '.' or '!')
            {
                sb.Append('\\');
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
