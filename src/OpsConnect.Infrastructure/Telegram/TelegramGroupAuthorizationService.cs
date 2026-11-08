using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Telegram;
using OpsConnect.Domain.Telegram;
using OpsConnect.Infrastructure.Persistence;

namespace OpsConnect.Infrastructure.Telegram;

/// <summary>
/// Evaluates Telegram group/supergroup authorisation against database-configured TelegramGroups
/// and TelegramBotOptions (AuthorizedSourceChatIds / DestinationChatIds).
///
/// Precedence Rule (Database-First):
/// 1. If any record exists in telegram.TelegramGroups for (TelegramChatId, GroupRole), the database
///    is the authoritative source of truth and overrides static configuration.
/// 2. An inactive or revoked database group record (or a group whose mapped Activity is inactive)
///    is strictly rejected even if the Chat ID still appears in static configuration.
/// 3. Static configuration (AuthorizedSourceChatIds / DestinationChatIds) is used only when no
///    database record exists for that (TelegramChatId, GroupRole).
///
/// Preserves Open Group Mode: authorisation is enforced at the group Chat ID level without requiring
/// individual employee registration or mobile-number mapping.
/// </summary>
public sealed class TelegramGroupAuthorizationService : ITelegramGroupAuthorizationService
{
    private readonly OpsConnectDbContext _dbContext;
    private readonly IOptionsMonitor<TelegramBotOptions> _optionsMonitor;

    public TelegramGroupAuthorizationService(
        OpsConnectDbContext dbContext,
        IOptionsMonitor<TelegramBotOptions> optionsMonitor)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(optionsMonitor);

        _dbContext = dbContext;
        _optionsMonitor = optionsMonitor;
    }

    public async Task<TelegramGroupAuthorizationResult> AuthorizeSourceGroupAsync(
        long telegramChatId,
        CancellationToken cancellationToken = default)
    {
        if (telegramChatId == 0)
        {
            return new TelegramGroupAuthorizationResult(
                IsAuthorized: false,
                TelegramChatId: telegramChatId,
                GroupRole: null,
                AuthorizationSource: null,
                AuthorizedActivityCodes: Array.Empty<string>(),
                RejectionReason: "TelegramChatId cannot be 0.");
        }

        // Query all database Source group records for this Chat ID (active or inactive)
        var allDbSourceGroups = await _dbContext.TelegramGroups
            .AsNoTracking()
            .Include(g => g.Activity)
            .Where(g => g.TelegramChatId == telegramChatId &&
                        g.GroupRole == TelegramGroupRole.Source)
            .ToListAsync(cancellationToken);

        // Precedence Rule 1: Database records exist for this Chat ID -> Database is authoritative
        if (allDbSourceGroups.Count > 0)
        {
            var activeDbSourceGroups = allDbSourceGroups
                .Where(g => g.IsActive && g.Activity.IsActive)
                .OrderBy(g => g.TelegramGroupId)
                .ToList();

            if (activeDbSourceGroups.Count == 0)
            {
                var firstDbGroup = allDbSourceGroups[0];
                return new TelegramGroupAuthorizationResult(
                    IsAuthorized: false,
                    TelegramChatId: telegramChatId,
                    GroupRole: TelegramGroupRole.Source,
                    AuthorizationSource: "Database",
                    AuthorizedActivityCodes: Array.Empty<string>(),
                    RejectionReason: $"Telegram source group '{telegramChatId}' ('{firstDbGroup.GroupTitle}') is inactive or its mapped activity is disabled in database records; database status takes precedence over static configuration.",
                    SourceTelegramGroupId: firstDbGroup.TelegramGroupId,
                    SourceGroupTitle: firstDbGroup.GroupTitle,
                    DefaultActivityId: firstDbGroup.ActivityId);
            }

            var primaryGroup = activeDbSourceGroups[0];
            var activityCodes = activeDbSourceGroups
                .Select(g => g.Activity.ActivityCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new TelegramGroupAuthorizationResult(
                IsAuthorized: true,
                TelegramChatId: telegramChatId,
                GroupRole: TelegramGroupRole.Source,
                AuthorizationSource: "Database",
                AuthorizedActivityCodes: activityCodes,
                RejectionReason: null,
                SourceTelegramGroupId: primaryGroup.TelegramGroupId,
                SourceGroupTitle: primaryGroup.GroupTitle,
                DefaultActivityId: primaryGroup.ActivityId);
        }

        // Precedence Rule 2: No database Source record exists -> Check static configuration fallback
        var options = _optionsMonitor.CurrentValue;
        bool isConfiguredSourceChat = options.AuthorizedSourceChatIds.Contains(telegramChatId);

        if (!isConfiguredSourceChat)
        {
            return new TelegramGroupAuthorizationResult(
                IsAuthorized: false,
                TelegramChatId: telegramChatId,
                GroupRole: null,
                AuthorizationSource: null,
                AuthorizedActivityCodes: Array.Empty<string>(),
                RejectionReason: $"Telegram chat '{telegramChatId}' is not registered as an active authorised source group.");
        }

        var defaultRffoActivity = await _dbContext.Activities
            .AsNoTracking()
            .SingleOrDefaultAsync(
                a => a.ActivityCode == ActivityModuleConstants.RffoActivityCode,
                cancellationToken);

        if (defaultRffoActivity is not null && !defaultRffoActivity.IsActive)
        {
            return new TelegramGroupAuthorizationResult(
                IsAuthorized: false,
                TelegramChatId: telegramChatId,
                GroupRole: TelegramGroupRole.Source,
                AuthorizationSource: "Configuration",
                AuthorizedActivityCodes: Array.Empty<string>(),
                RejectionReason: $"Default activity '{ActivityModuleConstants.RffoActivityCode}' is currently disabled.",
                DefaultActivityId: defaultRffoActivity.ActivityId);
        }

        return new TelegramGroupAuthorizationResult(
            IsAuthorized: true,
            TelegramChatId: telegramChatId,
            GroupRole: TelegramGroupRole.Source,
            AuthorizationSource: "Configuration",
            AuthorizedActivityCodes: [ActivityModuleConstants.RffoActivityCode],
            RejectionReason: null,
            SourceTelegramGroupId: null,
            SourceGroupTitle: null,
            DefaultActivityId: defaultRffoActivity?.ActivityId);
    }

    public async Task<bool> IsAuthorizedDestinationGroupAsync(
        long telegramChatId,
        string? activityCode = null,
        CancellationToken cancellationToken = default)
    {
        if (telegramChatId == 0)
        {
            return false;
        }

        var allDbDestinationGroups = await _dbContext.TelegramGroups
            .AsNoTracking()
            .Include(g => g.Activity)
            .Where(g => g.TelegramChatId == telegramChatId &&
                        g.GroupRole == TelegramGroupRole.Destination)
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(activityCode))
        {
            var trimmedCode = activityCode.Trim().ToUpperInvariant();
            allDbDestinationGroups = allDbDestinationGroups
                .Where(g => string.Equals(g.Activity.ActivityCode, trimmedCode, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (allDbDestinationGroups.Count > 0)
        {
            return allDbDestinationGroups.Any(g => g.IsActive && g.Activity.IsActive);
        }

        return _optionsMonitor.CurrentValue.DestinationChatIds.Contains(telegramChatId);
    }
}
