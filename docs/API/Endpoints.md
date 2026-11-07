# OpsConnect — API Specification

## 1. Overview

`OpsConnect.Api` exposes RESTful endpoints organized by functional area. Administrative endpoints are secured via role-based authorisation (`SuperAdmin`, `Admin`, `Viewer`), while the Telegram webhook endpoint validates the `X-Telegram-Bot-Api-Secret-Token` header.

---

## 2. Planned & Foundation Endpoints

### 2.1 System & Health (Phase 2 Foundation)
- `GET /api/health` — Returns service health and environment readiness status.

### 2.2 Telegram Webhook (Phase 5)
- `POST /api/telegram/webhook` — Receives updates from Telegram Bot API, validates secret token, checks source group authorisation, deduplicates via `(TelegramChatId, TelegramMessageId)`, processes branch updates, and sends confirmation/rejection replies.

### 2.3 Organisation Management (Phase 3 & Phase 7)
- `GET /api/branches` — Lists configured branches with status and hierarchy details.
- `GET /api/hierarchy` — Retrieves the `Zone → State → Region → Branch` tree.
- `POST /api/hierarchy/import` — Validates and imports organisation hierarchy from Excel.

### 2.4 RFFO Activity Endpoints (Phase 4 & Phase 7)
- `GET /api/rffo/status` — Retrieves current branch statuses for a given reporting date.
- `GET /api/rffo/summary` — Computes State-wise and Region-wise RFFO summary metrics.
- `GET /api/rffo/history/{branchId}` — Retrieves status transition audit history for a specific branch.

### 2.5 Administration & Reporting Configuration (Phase 6 & Phase 7)
- `GET /api/admin/configuration` — Retrieves system-level configurations.
- `PUT /api/admin/configuration` — Updates system-level configurations.
- `POST /api/admin/summary/trigger` — Manually triggers an on-demand summary report.
- `POST /api/admin/rffo/reopen` — Grants single-use permission to reopen a `Completed` branch for a specific reporting date with a mandatory reason.
- `GET /api/admin/rffo/reopen-history` — Lists granted, consumed, and expired reopen permissions.
- `GET /api/admin/activities/{activityId}/reporting-configuration` — Gets the schedule configuration for an activity.
- `PUT /api/admin/activities/{activityId}/reporting-configuration` — Updates start time, end time, interval, time zone, or `StopWhenAllCompleted`.
- `GET /api/admin/activities/{activityId}/reporting-cycle` — Inspects current cycle state, completion status, and slot logs.
- `POST /api/admin/activities/{activityId}/summary/trigger` — Triggers an immediate manual summary report for an activity.
