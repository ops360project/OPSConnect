namespace OpsConnect.Application.Telegram;

/// <summary>
/// Validates and normalises incoming Telegram Update payloads.
/// Safely classifies non-message updates (edited messages, channel posts, callback queries, membership changes,
/// non-text media/service messages, and bot-originated messages) and extracts Open Group Mode sender metadata.
/// </summary>
public sealed class TelegramUpdateNormalizer : ITelegramUpdateNormalizer
{
    public const int MaxAllowedMessageTextLength = 4_000;

    public TelegramUpdateNormalizationResult Normalize(TelegramWebhookUpdatePayload? update)
    {
        if (update is null)
        {
            return TelegramUpdateNormalizationResult.InvalidPayload(
                "Telegram update payload is null or empty.");
        }

        if (!update.UpdateId.HasValue || update.UpdateId.Value <= 0)
        {
            return TelegramUpdateNormalizationResult.InvalidPayload(
                "Telegram update payload is missing a valid positive 'update_id'.");
        }

        if (update.Message is null)
        {
            if (update.EditedMessage is not null)
            {
                return TelegramUpdateNormalizationResult.NonMessageUpdate(
                    "edited_message",
                    "Edited messages are ignored; branch operators must send a new standalone message.");
            }

            if (update.ChannelPost is not null || update.EditedChannelPost is not null)
            {
                return TelegramUpdateNormalizationResult.NonMessageUpdate(
                    "channel_post",
                    "Channel posts are ignored.");
            }

            if (update.CallbackQuery is not null)
            {
                return TelegramUpdateNormalizationResult.NonMessageUpdate(
                    "callback_query",
                    "Callback query updates are ignored.");
            }

            if (update.MyChatMember is not null || update.ChatMember is not null)
            {
                return TelegramUpdateNormalizationResult.NonMessageUpdate(
                    "chat_member",
                    "Chat membership updates are ignored.");
            }

            return TelegramUpdateNormalizationResult.NonMessageUpdate(
                "unsupported_update",
                "Update does not contain a standard 'message' object.");
        }

        var message = update.Message;

        if (!message.MessageId.HasValue || message.MessageId.Value <= 0)
        {
            return TelegramUpdateNormalizationResult.InvalidPayload(
                "Telegram message is missing a valid positive 'message_id'.");
        }

        if (message.Chat is null || !message.Chat.Id.HasValue || message.Chat.Id.Value == 0)
        {
            return TelegramUpdateNormalizationResult.InvalidPayload(
                "Telegram message is missing a valid 'chat.id'.");
        }

        if (!message.DateUnixSeconds.HasValue || message.DateUnixSeconds.Value <= 0)
        {
            return TelegramUpdateNormalizationResult.InvalidPayload(
                "Telegram message is missing a valid positive Unix 'date' timestamp.");
        }

        if (message.From?.IsBot == true)
        {
            return TelegramUpdateNormalizationResult.NonMessageUpdate(
                "bot_message",
                "Messages sent by bots are ignored.");
        }

        if (string.IsNullOrWhiteSpace(message.Text))
        {
            return TelegramUpdateNormalizationResult.NonMessageUpdate(
                "non_text_message",
                "Message does not contain text content (media, sticker, or service event ignored).");
        }

        var rawText = message.Text.Trim();
        if (rawText.Length > MaxAllowedMessageTextLength)
        {
            return TelegramUpdateNormalizationResult.InvalidPayload(
                $"Message text length ({rawText.Length}) exceeds maximum allowed length of {MaxAllowedMessageTextLength} characters.");
        }

        DateTime timestampUtc;
        try
        {
            timestampUtc = DateTimeOffset.FromUnixTimeSeconds(message.DateUnixSeconds.Value).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return TelegramUpdateNormalizationResult.InvalidPayload(
                "Telegram message 'date' Unix timestamp is out of valid range.");
        }

        var chatType = string.IsNullOrWhiteSpace(message.Chat.Type)
            ? "unknown"
            : message.Chat.Type.Trim().ToLowerInvariant();

        var chatTitle = string.IsNullOrWhiteSpace(message.Chat.Title)
            ? null
            : CollapseAndTruncate(message.Chat.Title, 250);

        var senderUsername = string.IsNullOrWhiteSpace(message.From?.Username)
            ? null
            : CollapseAndTruncate(message.From!.Username, 150);

        var senderDisplayName = BuildSenderDisplayName(message.From, senderUsername);

        var envelope = new NormalizedTelegramMessageEnvelope(
            UpdateId: update.UpdateId.Value,
            TelegramChatId: message.Chat.Id.Value,
            ChatType: chatType,
            ChatTitle: chatTitle,
            TelegramMessageId: message.MessageId.Value,
            TelegramUserId: message.From?.Id,
            IsSenderBot: message.From?.IsBot ?? false,
            SenderUsername: senderUsername,
            SenderDisplayName: senderDisplayName,
            MessageTimestampUtc: timestampUtc,
            RawMessageText: rawText);

        return TelegramUpdateNormalizationResult.ValidTextMessage(envelope);
    }

    private static string? BuildSenderDisplayName(TelegramWebhookUserPayload? from, string? fallbackUsername)
    {
        if (from is null)
        {
            return fallbackUsername;
        }

        var first = from.FirstName?.Trim();
        var last = from.LastName?.Trim();

        string? combined = (string.IsNullOrWhiteSpace(first), string.IsNullOrWhiteSpace(last)) switch
        {
            (false, false) => $"{first} {last}",
            (false, true) => first,
            (true, false) => last,
            _ => fallbackUsername
        };

        return string.IsNullOrWhiteSpace(combined)
            ? null
            : CollapseAndTruncate(combined, 200);
    }

    private static string CollapseAndTruncate(string value, int maxLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
