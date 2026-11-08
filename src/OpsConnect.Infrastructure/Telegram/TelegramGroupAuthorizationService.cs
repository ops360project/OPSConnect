using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpsConnect.Application.Activities.Common;
using OpsConnect.Application.Telegram;
using OpsConnect.Domain.Telegram;
using OpsConnect.Infrastructure.Persistence;

namespace OpsConnect.Infrastructure.Telegram;

/// <summary>
/// Evaluates Telegram group/supergroup authorisation against both database-configured TelegramGroups
/// and TelegramBotOptions (AuthorizedSourceChatIds / DestinationChatIds).
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

        var dbSourceGroups = await _dbContext.TelegramGroups
            .AsNoTracking()
            .Include(g => g.Activity)
            .Where(g => g.TelegramChatId == telegramChatId &&
                        g.GroupRole == TelegramGroupRole.Source &&
                        g.IsActive &&
                        g.Activity.IsActive)
            .ToListAsync(cancellationToken);

        var options = _optionsMonitor.CurrentValue;
        bool isConfiguredSourceChat = options.AuthorizedSourceChatIds.Contains(telegramChatId);

        if (dbSourceGroups.Count == 0 && !isConfiguredSourceChat)
        {
            return new TelegramGroupAuthorizationResult(
                IsAuthorized: false,
                TelegramChatId: telegramChatId,
                GroupRole: null,
                AuthorizationSource: null,
                AuthorizedActivityCodes: Array.Empty<string>(),
                RejectionReason: $"Telegram chat '{telegramChatId}' is not registered as an active authorised source group.");
        }

        var activityCodes = dbSourceGroups
            .Select(g => g.Activity.ActivityCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (activityCodes.Count == 0 && isConfiguredSourceChat)
        {
            activityCodes.Add(ActivityModuleConstants.RffoActivityCode);
        }

        string authSource = (dbSourceGroups.Count > 0, isConfiguredSourceChat) switch
        {
            (true, true) => "DatabaseAndConfiguration",
            (true, false) => "Database",
            _ => "Configuration"
        };

        return new TelegramGroupAuthorizationResult(
            IsAuthorized: true,
            TelegramChatId: telegramChatId,
            GroupRole: TelegramGroupRole.Source,
            AuthorizationSource: authSource,
            AuthorizedActivityCodes: activityCodes,
            RejectionReason: null);
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

        var query = _dbContext.TelegramGroups
            .AsNoTracking()
            .Include(g => g.Activity)
            .Where(g => g.TelegramChatId == telegramChatId &&
                        g.GroupRole == TelegramGroupRole.Destination &&
                        g.IsActive &&
                        g.Activity.IsActive);

        if (!string.IsNullOrWhiteSpace(activityCode))
        {
            var trimmedCode = activityCode.Trim().ToUpperInvariant();
            query = query.Where(g => g.Activity.ActivityCode == trimmedCode);
        }

        if (await query.AnyAsync(cancellationToken))
        {
            return true;
        }

        return _optionsMonitor.CurrentValue.DestinationChatIds.Contains(telegramChatId);
    }
}
