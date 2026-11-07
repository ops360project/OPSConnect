# OpsConnect — API Specification

## 1. Overview

`OpsConnect.Api` exposes RESTful JSON endpoints organized by functional domain.
- **OpenAPI / Swagger UI**: Available in Development at `/swagger` (`/openapi/v1.json`).
- **Authentication & Role-Based Authorisation**:
  - Protected endpoints authenticate via the `OpsConnectAuth` scheme (`X-OpsConnect-Api-Key`, `X-OpsConnect-User`, `X-OpsConnect-Role`; to be extended in Phase 7 with JWT bearer authentication).
  - Unauthenticated requests to protected endpoints receive `401 Unauthorized`.
  - Authenticated requests lacking the required role receive `403 Forbidden`.
  - **Roles**:
    - `SuperAdmin` — Full read and administrative write access.
    - `Admin` — Operational read and administrative write access (including hierarchy imports and branch status reopening).
    - `Viewer` — Read-only access to branches, hierarchy trees, and historical mapping queries.

---

## 2. System & Health Endpoints

### `GET /api/health`
- **Access**: Anonymous
- **Purpose**: Returns service health and current UTC / `Asia/Kolkata` business date.
- **Response (`200 OK`)**:
```json
{
  "service": "OpsConnect.Api",
  "status": "Healthy",
  "utcNow": "2026-11-07T17:30:00Z",
  "businessDateAsiaKolkata": "2026-11-07"
}
```

---

## 3. Organisation Master Endpoints (Phase 3)

### 3.1 `GET /api/branches`
Retrieves configured branches ordered by `ZoneCode → StateCode → RegionCode → BranchCode`.

- **Method & Path**: `GET /api/branches`
- **Authorisation Policy**: `OrganisationRead` (Roles: `SuperAdmin`, `Admin`, `Viewer`)
- **Query Parameters**:
  | Parameter | Type | Required | Description |
  |---|---|---|---|
  | `isActive` | `bool?` | No | Filter by active (`true`) or inactive (`false`) branch status |
  | `zoneCode` | `string?` | No | Filter by Zone code (case-insensitive, e.g., `EAST`) |
  | `stateCode` | `string?` | No | Filter by State code (case-insensitive, e.g., `OD`) |
  | `regionCode` | `string?` | No | Filter by Region code (case-insensitive, e.g., `REG-A`) |
  | `searchTerm` | `string?` | No | Case-insensitive substring match against `BranchCode` or `BranchName` |
- **Status Codes**:
  - `200 OK` — Returns `IReadOnlyList<BranchSummaryDto>`
  - `401 Unauthorized` — Missing or invalid authentication credentials
  - `403 Forbidden` — Authenticated role is not permitted
- **Example Response (`200 OK`)**:
```json
[
  {
    "branchId": 1,
    "branchCode": "36:01",
    "branchName": "Example Branch",
    "isActive": true,
    "regionId": 10,
    "regionCode": "REG-A",
    "regionName": "Region A",
    "isRegionActive": true,
    "stateId": 5,
    "stateCode": "OD",
    "stateName": "Odisha",
    "isStateActive": true,
    "zoneId": 1,
    "zoneCode": "EAST",
    "zoneName": "East Zone",
    "isZoneActive": true,
    "canSubmitActivityUpdates": true,
    "createdAtUtc": "2026-11-07T17:00:00Z",
    "updatedAtUtc": null
  }
]
```

---

### 3.2 `GET /api/hierarchy`
Retrieves the nested four-tier `Zone → State → Region → Branch` hierarchy tree.

- **Method & Path**: `GET /api/hierarchy`
- **Authorisation Policy**: `OrganisationRead` (Roles: `SuperAdmin`, `Admin`, `Viewer`)
- **Query Parameters**:
  | Parameter | Type | Default | Description |
  |---|---|---|---|
  | `includeInactive` | `bool` | `false` | When `false`, returns only active Zones, States, Regions, and Branches. When `true`, includes inactive nodes. |
- **Status Codes**:
  - `200 OK` — Returns `IReadOnlyList<ZoneHierarchyNodeDto>`
  - `401 Unauthorized` / `403 Forbidden`
- **Example Response (`200 OK`)**:
```json
[
  {
    "zoneId": 1,
    "zoneCode": "EAST",
    "zoneName": "East Zone",
    "isActive": true,
    "states": [
      {
        "stateId": 5,
        "stateCode": "OD",
        "stateName": "Odisha",
        "isActive": true,
        "regions": [
          {
            "regionId": 10,
            "regionCode": "REG-A",
            "regionName": "Region A",
            "isActive": true,
            "branches": [
              {
                "branchId": 1,
                "branchCode": "36:01",
                "branchName": "Example Branch",
                "isActive": true
              }
            ]
          }
        ]
      }
    ]
  }
]
```

---

### 3.3 `GET /api/branches/{branchCode}/history`
Retrieves the complete effective-dated hierarchy mapping history (`org.OrganisationMappingHistory`) for a branch and resolves the exact hierarchy mapping that was effective on a specified `asOfReportingDate`.

- **Method & Path**: `GET /api/branches/{branchCode}/history`
- **Authorisation Policy**: `OrganisationRead` (Roles: `SuperAdmin`, `Admin`, `Viewer`)
- **Route & Query Parameters**:
  | Parameter | Source | Type | Required | Description |
  |---|---|---|---|---|
  | `branchCode` | Route | `string` | Yes | Company branch identifier (e.g., `36:01`) |
  | `asOfReportingDate` | Query | `DateOnly?` (`yyyy-MM-dd`) | No | Reporting date for historical hierarchy resolution (defaults to current `Asia/Kolkata` business date) |
- **Effective-Date & Historical Mapping Semantics**:
  - Each branch maintains effective-dated snapshots (`EffectiveFromDate` to `EffectiveToDate`, where `EffectiveToDate = null` denotes the current active mapping).
  - Moving a branch to a new Region, State, or Zone on `effectiveFromDate` closes the previous record as of `effectiveFromDate - 1 day` and opens a new record starting on `effectiveFromDate`.
  - Querying a historical `asOfReportingDate` resolves the snapshot matching `EffectiveFromDate <= asOfReportingDate && (EffectiveToDate == null || EffectiveToDate >= asOfReportingDate)`, ensuring historical reports never change when branches are reorganized later.
- **Status Codes**:
  - `200 OK` — Returns `BranchMappingHistoryResultDto`
  - `400 Bad Request` — Blank or invalid `branchCode`
  - `404 Not Found` — Branch code does not exist (`ProblemDetails`)
  - `401 Unauthorized` / `403 Forbidden`
- **Example Response (`200 OK`)**:
```json
{
  "branchId": 1,
  "branchCode": "36:01",
  "branchName": "Example Branch",
  "isBranchActive": true,
  "asOfReportingDate": "2026-11-05",
  "resolvedMappingAsOfDate": {
    "mappingHistoryId": 100,
    "branchId": 1,
    "branchCode": "36:01",
    "zoneId": 1,
    "zoneCode": "EAST",
    "zoneName": "East Zone",
    "stateId": 5,
    "stateCode": "OD",
    "stateName": "Odisha",
    "regionId": 10,
    "regionCode": "REG-A",
    "regionName": "Region A",
    "effectiveFromDate": "2026-11-01",
    "effectiveToDate": "2026-11-09",
    "changeReason": "Initial hierarchy import",
    "recordedAtUtc": "2026-11-01T04:00:00Z",
    "recordedBy": "admin",
    "isCurrentActiveMapping": false
  },
  "historyEntries": [
    {
      "mappingHistoryId": 101,
      "branchId": 1,
      "branchCode": "36:01",
      "zoneId": 1,
      "zoneCode": "EAST",
      "zoneName": "East Zone",
      "stateId": 5,
      "stateCode": "OD",
      "stateName": "Odisha",
      "regionId": 11,
      "regionCode": "REG-B",
      "regionName": "Region B",
      "effectiveFromDate": "2026-11-10",
      "effectiveToDate": null,
      "changeReason": "Regional boundary realignment",
      "recordedAtUtc": "2026-11-10T04:00:00Z",
      "recordedBy": "admin",
      "isCurrentActiveMapping": true
    },
    {
      "mappingHistoryId": 100,
      "branchId": 1,
      "branchCode": "36:01",
      "zoneId": 1,
      "zoneCode": "EAST",
      "zoneName": "East Zone",
      "stateId": 5,
      "stateCode": "OD",
      "stateName": "Odisha",
      "regionId": 10,
      "regionCode": "REG-A",
      "regionName": "Region A",
      "effectiveFromDate": "2026-11-01",
      "effectiveToDate": "2026-11-09",
      "changeReason": "Initial hierarchy import",
      "recordedAtUtc": "2026-11-01T04:00:00Z",
      "recordedBy": "admin",
      "isCurrentActiveMapping": false
    }
  ]
}
```

---

### 3.4 `POST /api/hierarchy/import`
Validates and imports an organisation hierarchy from an uploaded `.xlsx` spreadsheet.

- **Method & Path**: `POST /api/hierarchy/import`
- **Content-Type**: `multipart/form-data`
- **Authorisation Policy**: `OrganisationManage` (Roles: `SuperAdmin`, `Admin` — `Viewer` receives `403 Forbidden`)
- **Form & Query Parameters**:
  | Parameter | Source | Type | Default | Description |
  |---|---|---|---|---|
  | `file` | Form (`multipart/form-data`) | `IFormFile` | *(Required)* | Excel `.xlsx` file containing the hierarchy mapping |
  | `validateOnly` | Query | `bool` | `false` | When `true`, runs full batch & database-transition validation and returns the validation report without persisting any database changes (`wasPersisted: false`) |
  | `effectiveFromDate` | Query | `DateOnly?` (`yyyy-MM-dd`) | Current `Asia/Kolkata` date | Effective start date for newly created or updated hierarchy mappings |
  | `changeReason` | Query | `string?` | `null` | Audit reason recorded in `org.OrganisationMappingHistory` |
  | `sheetName` | Query | `string?` | First sheet | Optional worksheet name to read |

#### Supported Excel Format & Column Headers
Header matching is case-insensitive and ignores spaces, underscores, and hyphens:
- **Zone Code**: `ZoneCode`, `Zone Code`, `Zone_Code`, `ZoneID`, `Zone`
- **Zone Name**: `ZoneName`, `Zone Name`, `Zone_Name`, `Zone`
- **State Code**: `StateCode`, `State Code`, `State_Code`, `StateID`, `State`
- **State Name**: `StateName`, `State Name`, `State_Name`, `State`
- **Region Code**: `RegionCode`, `Region Code`, `Region_Code`, `RegionID`, `Region`
- **Region Name**: `RegionName`, `Region Name`, `Region_Name`, `Region`
- **Branch Code**: `BranchCode`, `Branch Code`, `Branch_Code`, `BranchID`, `Branch ID`, `Branch`
- **Branch Name**: `BranchName`, `Branch Name`, `Branch_Name`
- **Active Flags (Optional, default `true`)**:
  - Branch: `IsBranchActive`, `BranchActive`, `BranchStatus`, `IsActive`, `Active`, `Status` (`Active`/`Inactive`, `true`/`false`, `1`/`0`, `Yes`/`No`)
  - Parent hierarchy: `IsZoneActive`/`ZoneActive`/`ZoneStatus`, `IsStateActive`/`StateActive`/`StateStatus`, `IsRegionActive`/`RegionActive`/`RegionStatus`

#### Validation Rules & Error Codes (`OrganisationImportErrorCode`)
If any row fails validation, **zero database records are written** (`wasPersisted: false`) and the endpoint returns `400 Bad Request` containing the full `OrganisationImportExecutionResult` with row-level `errors`:
- `EmptyImportBatch` — Spreadsheet has no data rows.
- `MissingRequiredField` — Required code or name column is missing/blank.
- `InvalidCodeFormat` — Code contains internal whitespace (e.g. `"36 : 01"`).
- `FieldLengthExceeded` — Code exceeds 50 chars or name exceeds 150/200 chars.
- `DuplicateBranchCode` — The same `BranchCode` appears more than once in the file.
- `ConflictingZoneDefinition` — Same `ZoneCode` has conflicting name or active status across rows.
- `ConflictingStateHierarchyMapping` — Same `StateCode` is mapped to multiple `ZoneCode`s in the file.
- `ConflictingRegionHierarchyMapping` — Same `RegionCode` is mapped to multiple `StateCode`s in the file.
- `InactiveParentHierarchy` — Active child mapped under an inactive parent Zone, State, or Region.
- `InvalidEffectiveDateTransition` — `effectiveFromDate` is earlier than an existing branch's current active mapping start date.

#### Status Codes
- `200 OK` — Import validated (and persisted within an atomic transaction when `validateOnly=false`).
- `400 Bad Request` — Missing/non-`.xlsx` file (`ProblemDetails`) or validation errors in spreadsheet (`OrganisationImportExecutionResult` with `validationReport.isValid = false`).
- `401 Unauthorized` — Missing or invalid credentials.
- `403 Forbidden` — Caller role (e.g. `Viewer`) is not authorised for `OrganisationManage`.

#### Example Response (`200 OK`)
```json
{
  "validateOnly": false,
  "wasPersisted": true,
  "effectiveFromDate": "2026-11-07",
  "validationReport": {
    "totalRowsRead": 2,
    "validRowCount": 2,
    "invalidRowCount": 0,
    "isValid": true,
    "distinctZonesCount": 1,
    "distinctStatesCount": 1,
    "distinctRegionsCount": 1,
    "distinctBranchesCount": 2,
    "errors": [],
    "validatedRows": [
      {
        "rowNumber": 2,
        "zoneCode": "EAST",
        "zoneName": "East Zone",
        "isZoneActive": true,
        "stateCode": "OD",
        "stateName": "Odisha",
        "isStateActive": true,
        "regionCode": "REG-A",
        "regionName": "Region A",
        "isRegionActive": true,
        "branchCode": "36:01",
        "branchName": "Example Branch 1",
        "isBranchActive": true
      }
    ]
  },
  "zonesCreated": 1,
  "zonesUpdated": 0,
  "statesCreated": 1,
  "statesUpdated": 0,
  "regionsCreated": 1,
  "regionsUpdated": 0,
  "branchesCreated": 2,
  "branchesUpdated": 0,
  "mappingHistoriesCreated": 2
}
```

---

## 4. Planned Future Phase Endpoints (Phases 4–7)

### Telegram Webhook (Phase 5)
- `POST /api/telegram/webhook`

### RFFO Activity Endpoints (Phase 4 & Phase 7)
- `GET /api/rffo/status`
- `GET /api/rffo/summary`
- `GET /api/rffo/history/{branchId}`

### Administration & Reporting Configuration (Phase 6 & Phase 7)
- `GET /api/admin/configuration`
- `PUT /api/admin/configuration`
- `POST /api/admin/summary/trigger`
- `POST /api/admin/rffo/reopen`
- `GET /api/admin/rffo/reopen-history`
- `GET /api/admin/activities/{activityId}/reporting-configuration`
- `PUT /api/admin/activities/{activityId}/reporting-configuration`
- `GET /api/admin/activities/{activityId}/reporting-cycle`
- `POST /api/admin/activities/{activityId}/summary/trigger`
