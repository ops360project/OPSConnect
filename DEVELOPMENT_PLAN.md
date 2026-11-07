# OpsConnect — Master Development Plan

## 1. Executive Summary

**OpsConnect** is a modular, extensible operational activity monitoring and reporting platform built using a **Modular Monolith** architecture on **ASP.NET Core (.NET 10.0)** and **Microsoft SQL Server**.

Its primary initial objective is to automate the collection, validation, status tracking, and scheduled/immediate summary reporting of **RFFO (Request for Field Force Operations)** activity updates submitted by branches via **Telegram**, while providing a clean, multi-activity architectural foundation to support future activities (such as **GetData**), a web dashboard, and an Android administrative application.

---

## 2. Environment Baseline & Technical Decisions

### 2.1 Inspected Local Environment
- **Operating System**: Windows (x64)
- **Installed .NET SDK**: `10.0.403` (`C:\Program Files\dotnet\sdk\10.0.403`)
- **Installed .NET Runtimes**:
  - `Microsoft.NETCore.App 10.0.14`
  - `Microsoft.AspNetCore.App 10.0.14`
  - `Microsoft.WindowsDesktop.App 10.0.14`
- **Target Framework Selection**: `net10.0` (.NET 10 LTS). Selected because `.NET SDK 10.0.403` is the sole installed SDK on the machine and .NET 10 is a Long-Term Support (LTS) release suitable for production enterprise workloads.
- **SQL Server Environment**:
  - Default instance `MSSQLSERVER` (`MSSQL17.MSSQLSERVER`) — **Running**
  - Named instances `MSSQL$MSSQLSERVER01`, `MSSQL$MSSQLSERVER02` — **Running**
  - SQL Server Browser & SQL Server Agent (`SQLSERVERAGENT`) — **Running**
  - SQL Server LocalDB (`MSSQLLocalDB`) — **Installed**
- **IDEs & Tooling**:
  - Visual Studio (`2022` and `18` / 2026)
  - Git `2.56.0.windows.2`

### 2.2 Key Architectural Decisions
1. **Modular Monolith over Microservices**: All domain logic, persistence, API controllers, and background workers live within a single unified solution (`OpsConnect.sln`) divided into strict architectural layers (`Domain`, `Application`, `Infrastructure`, `Api`, `Worker`). This keeps deployment and maintenance straightforward for an IT operations team while enforcing clean separation of concerns.
2. **Schema-Segregated SQL Server Database (`OpsConnectDB`)**: Entities are organized into five logical SQL Server schemas (`org`, `activity`, `telegram`, `reporting`, `admin`) managed strictly through Entity Framework Core migrations.
3. **Time Zone Standardisation**:
   - All database system timestamps (`CreatedAtUtc`, `UpdatedAtUtc`, etc.) are stored in **UTC** (`datetime2`).
   - Business reporting dates (`ReportingDate`, stored as `date`) and scheduled reporting slots are evaluated in the activity's configured time zone (defaulting to **`Asia/Kolkata`**).
4. **Durable Background Scheduling & Idempotent Delivery**:
   - Scheduled and immediate final reports are separated into two distinct phases: **Report Generation** (`reporting.SummaryLog`) and **Report Delivery** (`reporting.ReportDeliveryLog`).
   - Database-level unique constraints guarantee idempotency (`ActivityId + ReportingDate + ScheduledSlot + DestinationGroupId` for scheduled reports, and a unique cycle completion trigger for immediate final reports).
5. **Open-Group Telegram Submission Model**:
   - Any member in an authorised source Telegram group can post branch updates without prior employee registration.
   - Duplicate detection uses a composite unique index on `(TelegramChatId, TelegramMessageId)`.
   - Full sender metadata (`TelegramUserId`, `Username`, `DisplayName`, `ChatId`, `MessageId`, raw text) is retained for auditability.

---

## 3. Phased Implementation Roadmap

### Phase 1 — Environment Inspection and Planning *(Completed)*
- [x] Inspect installed .NET SDKs, runtimes, Visual Studio, and SQL Server instances.
- [x] Select target framework (`net10.0`) and document rationale.
- [x] Prepare `DEVELOPMENT_PLAN.md` and `README.md`.
- [x] Prepare architecture, database, API, Telegram, and deployment documentation under `docs/`.

### Phase 2 — Solution and Database Foundation *(Completed)*
- [x] Create `OpsConnect.sln` with 5 core projects (`src/`) and 2 test projects (`tests/`).
- [x] Configure strict project dependency references (`Domain` ← `Application` ← `Infrastructure` ← `Api` / `Worker`).
- [x] Install and verify compatible NuGet packages (EF Core SQL Server, Hangfire, OpenAPI/Swagger, xUnit).
- [x] Implement initial domain entities and enums across all five functional domains (`Organisation`, `Activities`, `Telegram`, `Reporting`, `Administration`).
- [x] Implement `OpsConnectDbContext` and explicit `IEntityTypeConfiguration<T>` classes mapping tables, schemas, primary keys, foreign keys, indexes, concurrency tokens, and unique constraints.
- [x] Generate initial EF Core migration (`InitialFoundation`).
- [x] Create `OpsConnectDB` on local SQL Server and apply the initial migration.
- [x] Build the complete solution and pass initial unit and integration/foundation tests.

> **STOP GATE**: Execution pauses after Phase 2 for explicit project owner review and approval before starting Phase 3.

---

### Phase 3 — Organisation Master *(Pending Approval)*
- Implement `Zone`, `State`, `Region`, and `Branch` management services in `OpsConnect.Application`.
- Prepare the Excel hierarchy import pipeline and validation report generator (without inventing real organisation data).
- Enforce hierarchy validation rules: unique branch codes (e.g., `36:01`), active status checks, invalid mapping rejection, and effective-dated `OrganisationMappingHistory` tracking so historical reports remain immutable.
- Add unit and integration tests covering valid/invalid organisation imports and hierarchy queries.

### Phase 4 — Activity and RFFO Foundation *(Pending Approval)*
- Implement generic `ActivityMaster` management and pluggable activity handlers (`IActivityUpdateHandler`).
- Implement the standalone Telegram message parser supporting case-insensitive, whitespace-tolerant parsing of `Activity`, `BranchID`, and `Status`.
- Implement RFFO daily status lifecycle (`NotReported`, `Pending`, `Completed`) where each reporting date begins cleanly at `NotReported`.
- Enforce the status transition matrix (`Completed` → `Pending` rejected by default).
- Implement transaction-safe administrator `StatusReopenPermission` granting, single-use consumption, mandatory reason logging, concurrency protection (`rowversion`), and end-of-day expiry.
- Add comprehensive unit tests for parsing, transitions, and reopening permissions.

### Phase 5 — Telegram Integration *(Pending Approval)*
- Implement `POST /api/telegram/webhook` with `X-Telegram-Bot-Api-Secret-Token` validation.
- Implement authorised source group validation against `telegram.TelegramGroups`.
- Implement idempotent message ingestion (`TelegramChatId + TelegramMessageId` uniqueness).
- Connect webhook pipeline to the RFFO processor and send clear confirmation/rejection replies via Telegram Bot API.
- Add integration tests for webhook security, duplicate message suppression, and open-group audit logging.

### Phase 6 — Reporting Engine *(Pending Approval)*
- Implement dynamic slot calculation from `admin.ActivityReportingConfiguration` (e.g., 09:00 to 18:00 every 30 minutes in `Asia/Kolkata`).
- Configure Hangfire with SQL Server storage in `OpsConnect.Worker` for durable slot scheduling and restart recovery.
- Implement State-wise and Region-wise summary calculation (`Completed + Pending + NotReported = Total`) using consistent database snapshots.
- Implement multi-part Telegram report splitting for large summaries while preserving headers and overall totals.
- Implement **Immediate Final Report** trigger when all active mapped branches reach `Completed` (with non-empty branch population guard, transaction-safe idempotency, and automatic stopping of subsequent scheduled slots with reason `AllBranchesCompleted`).
- Implement **Reporting Cycle Reactivation** when an authorised reopening permission is consumed by a valid `Pending` update, resuming only future configured slots.
- Implement independent delivery tracking (`reporting.ReportDeliveryLog`) and retry policies per destination group.

### Phase 7 — Administration and Security *(Pending Approval)*
- Implement JWT / API authentication and role-based authorisation (`SuperAdmin`, `Admin`, `Viewer`).
- Expose administrative REST endpoints for organisation data, activity reporting schedules, manual report triggers, branch status reopening, and audit log inspection.
- Add API authorisation and end-to-end integration tests.

### Phase 8 — Future Development *(Future Scope)*
- Implement `GetData` and additional operational activities.
- Build the Web Dashboard and Android Administrative Application consuming `OpsConnect.Api`.
- Production hardening, monitoring, and deployment automation.

---

## 4. Prerequisites & Manual Configuration Guide

1. **.NET 10 SDK**: Installed (`10.0.403`).
2. **SQL Server**: Local SQL Server instance running (`localhost` default instance `MSSQLSERVER`). Connection string configured under `ConnectionStrings:OpsConnectDb`.
3. **Telegram Bot Credentials (Required in Phase 5)**:
   - Obtain a Bot Token via `@BotFather` on Telegram.
   - Generate a cryptographic webhook secret token.
   - Store credentials via .NET User Secrets (`dotnet user-secrets set "Telegram:BotToken" "<TOKEN>"`) or environment variables (`Telegram__BotToken`, `Telegram__WebhookSecretToken`). **Never commit tokens to Git.**
4. **Organisation Master Excel File (Required in Phase 3)**:
   - To be provided by the project owner containing actual `Zone → State → Region → Branch` mappings.
