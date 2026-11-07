# OpsConnect — Telegram Integration Guide

## 1. Communication Model

OpsConnect uses **Telegram** as its operational messaging channel.
- **Source Groups**: Dedicated Telegram groups where branch staff submit activity updates using an **open-group submission model** (no employee registration required).
- **Destination Groups**: Dedicated Telegram groups where OpsConnect delivers scheduled and immediate final summary reports.

---

## 2. Expected Message Format (RFFO)

### Pending Submission
```text
Activity- RFFO
BranchID- 36:01
Status- Pending
```

### Completed Submission
```text
Activity- RFFO
BranchID- 36:01
Status- Completed
```

The message parser tolerates reasonable variations in casing, spaces around separators, and leading/trailing whitespace, while strictly rejecting incomplete or ambiguous messages.

---

## 3. Security & Audit Requirements

1. **Secret Token Validation**: All incoming requests to `/api/telegram/webhook` must include a valid `X-Telegram-Bot-Api-Secret-Token` header matching the configured secret.
2. **Source Group Authorisation**: Updates are accepted only from active `Source` groups configured in `telegram.TelegramGroups` for the specified activity.
3. **Idempotency**: Duplicate webhook deliveries are detected via the unique index on `(TelegramChatId, TelegramMessageId)` in `telegram.TelegramMessageLog`.
4. **Sender Audit**: Every message logs `TelegramUserId`, `SenderUsername`, `SenderDisplayName`, `TelegramChatId`, `TelegramMessageId`, `MessageTimestampUtc`, raw message text, and processing outcome.
5. **Secret Management**: Never store the Telegram Bot Token or Webhook Secret Token in source control. Use `.NET User Secrets` in local development or environment variables in production.
