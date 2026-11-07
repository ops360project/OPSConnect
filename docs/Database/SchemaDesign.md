# OpsConnect — Database Schema & Design

## 1. Database Overview

- **Database Name**: `OpsConnectDB`
- **EF Core DbContext**: `OpsConnectDbContext`
- **Connection String Key**: `OpsConnectDb`
- **Time Handling**:
  - System timestamps (`*AtUtc`) are stored as `datetime2` in **UTC**.
  - Operational reporting dates (`ReportingDate`) are stored as `date` in business time zone (`Asia/Kolkata`).

---

## 2. Schemas and Tables

### 2.1 `org` Schema (Organisation Master & Hierarchy)
| Table | Primary Key | Unique Constraints / Indexes | Purpose |
|---|---|---|---|
| `org.ZoneMaster` | `ZoneId` (`int`) | `UX_ZoneMaster_ZoneCode` (`ZoneCode`) | Top-level organisational zone |
| `org.StateMaster` | `StateId` (`int`) | `UX_StateMaster_StateCode` (`StateCode`), `IX_StateMaster_ZoneId` | State linked to a Zone |
| `org.RegionMaster` | `RegionId` (`int`) | `UX_RegionMaster_RegionCode` (`RegionCode`), `IX_RegionMaster_StateId` | Region linked to a State |
| `org.BranchMaster` | `BranchId` (`int`) | `UX_BranchMaster_BranchCode` (`BranchCode`, e.g. `36:01`), `IX_BranchMaster_RegionId` | Operational branch linked to Region, State, and Zone |
| `org.OrganisationMappingHistory` | `MappingHistoryId` (`bigint`) | `IX_OrganisationMappingHistory_BranchId_EffectiveFromDate` | Effective-dated historical record of branch-to-region/state/zone mapping so past reports remain accurate |

### 2.2 `activity` Schema (Operational Activities & Status Lifecycle)
| Table | Primary Key | Unique Constraints / Indexes | Purpose |
|---|---|---|---|
| `activity.ActivityMaster` | `ActivityId` (`int`) | `UX_ActivityMaster_ActivityCode` (`ActivityCode`, e.g. `RFFO`) | Generic registry of operational activities |
| `activity.ActivityStatus` | `ActivityStatusId` (`bigint`) | `UX_ActivityStatus_Activity_Branch_Date` (`ActivityId`, `BranchId`, `ReportingDate`) | Current status (`NotReported`, `Pending`, `Completed`) per branch, activity, and date (`RowVersion` protected) |
| `activity.ActivityHistory` | `ActivityHistoryId` (`bigint`) | `IX_ActivityHistory_Activity_Branch_Date` | Immutable audit trail of every status change and submission |
| `activity.StatusReopenPermission` | `PermissionId` (`bigint`) | `UX_StatusReopenPermission_ActivePerBranchDate` (filtered unique where `[PermissionStatus] = 'Available'`), `IX_StatusReopenPermission_Lookup` | Single-use administrator permission to transition `Completed` → `Pending` (`RowVersion` protected) |

### 2.3 `telegram` Schema (Telegram Groups & Message Audit)
| Table | Primary Key | Unique Constraints / Indexes | Purpose |
|---|---|---|---|
| `telegram.TelegramGroups` | `TelegramGroupId` (`int`) | `UX_TelegramGroups_ChatId_Activity_Role` (`TelegramChatId`, `ActivityId`, `GroupRole`) | Configured Source and Destination Telegram groups mapped to activities |
| `telegram.TelegramMessageLog` | `TelegramMessageLogId` (`bigint`) | `UX_TelegramMessageLog_ChatId_MessageId` (`TelegramChatId`, `TelegramMessageId`) | Audit log and duplicate-detection registry of all inbound Telegram messages |

### 2.4 `reporting` Schema (Cycles, Slots, Summaries & Delivery)
| Table | Primary Key | Unique Constraints / Indexes | Purpose |
|---|---|---|---|
| `reporting.ReportingCycle` | `ReportingCycleId` (`bigint`) | `UX_ReportingCycle_Activity_Date` (`ActivityId`, `ReportingDate`) | Tracks daily reporting lifecycle (`Active`, `Completed`, reactivation history, stop reason, `RowVersion`) |
| `reporting.ReportingSlot` | `ReportingSlotId` (`bigint`) | `UX_ReportingSlot_Activity_Date_SlotTime` (`ActivityId`, `ReportingDate`, `ScheduledSlotTime`) | Durable scheduled reporting slot state (`RowVersion` protected) |
| `reporting.SummaryLog` | `ReportId` (`bigint`) | `UX_SummaryLog_IdempotencyKey` (`IdempotencyKey`) | Generated summary snapshot and totals before delivery |
| `reporting.ReportDeliveryLog` | `ReportDeliveryId` (`bigint`) | `UX_ReportDeliveryLog_IdempotencyKey` (`IdempotencyKey`), `UX_ReportDeliveryLog_ReportId_DestinationGroupId` (`ReportId`, `DestinationGroupId`) | Per-destination group delivery status, retry counter, Telegram message IDs, and error tracking |

### 2.5 `admin` Schema (Security & Configuration)
| Table | Primary Key | Unique Constraints / Indexes | Purpose |
|---|---|---|---|
| `admin.UserMaster` | `UserId` (`int`) | `UX_UserMaster_Username`, `UX_UserMaster_Email` | Administrative users |
| `admin.RoleMaster` | `RoleId` (`int`) | `UX_RoleMaster_RoleName` (`SuperAdmin`, `Admin`, `Viewer`) | Security roles |
| `admin.UserRoleMapping` | `UserRoleMappingId` (`int`) | `UX_UserRoleMapping_UserId_RoleId` | Many-to-many user-role assignment |
| `admin.SystemConfiguration` | `SystemConfigurationId` (`int`) | `UX_SystemConfiguration_ConfigKey` | Global key-value settings |
| `admin.ActivityConfiguration` | `ActivityConfigurationId` (`int`) | `UX_ActivityConfiguration_ActivityId_ConfigKey` | Activity-specific key-value settings |
| `admin.ActivityReportingConfiguration` | `ConfigurationId` (`int`) | `IX_ActivityReportingConfiguration_Activity_Effective` | Configurable start/end times, interval minutes, time zone (`Asia/Kolkata`), and `StopWhenAllCompleted` |
| `admin.ConfigurationHistory` | `ConfigurationHistoryId` (`bigint`) | `IX_ConfigurationHistory_Entity_ChangedAtUtc` | Audit log of all administrative configuration changes |
