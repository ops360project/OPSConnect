# OpsConnect

**OpsConnect** is a modular, extensible operational activity monitoring and reporting platform built with **ASP.NET Core (.NET 10.0)**, **Entity Framework Core**, **Microsoft SQL Server**, **Hangfire**, and the **Telegram Bot API**.

It replaces manual Telegram-based follow-ups with an automated, auditable, and configurable workflow for collecting branch-level operational updates (starting with **RFFO — Request for Field Force Operations**), tracking real-time status across the `Zone → State → Region → Branch` hierarchy, and delivering scheduled as well as immediate completion summary reports.

---

## Solution Structure

```text
OpsConnect/
├── OpsConnect.sln
├── src/
│   ├── OpsConnect.Domain/          # Enterprise domain entities, enums, value objects, and business rules
│   ├── OpsConnect.Application/     # Use cases, interfaces, DTOs, validators, and orchestration contracts
│   ├── OpsConnect.Infrastructure/  # EF Core DbContext, SQL Server migrations, repositories, Telegram & Hangfire adapters
│   ├── OpsConnect.Api/             # ASP.NET Core Web API, Telegram webhook, admin controllers, OpenAPI/Swagger
│   └── OpsConnect.Worker/          # Background worker host for scheduled reporting, immediate final reports, and retries
├── tests/
│   ├── OpsConnect.UnitTests/       # Fast, isolated domain and application unit tests (xUnit)
│   └── OpsConnect.IntegrationTests/# Database mapping, schema, and persistence verification tests (xUnit)
├── docs/
│   ├── Architecture/               # Modular Monolith architecture and design decisions
│   ├── Database/                   # Schema design, ER relationships, and migration guide
│   ├── API/                        # REST API and Webhook endpoint specifications
│   ├── Telegram/                   # Bot setup, open-group submission format, and security model
│   └── Deployment/                 # Local setup, secret management, and operational runbook
├── scripts/                        # Helper utility scripts
└── DEVELOPMENT_PLAN.md             # Phased implementation roadmap and current status
```

---

## Technology Stack

| Layer | Technology |
|---|---|
| Runtime & SDK | .NET 10.0 (`net10.0`, SDK `10.0.403`) |
| Web Framework | ASP.NET Core Web API with OpenAPI / Swagger |
| Database | Microsoft SQL Server (`OpsConnectDB`) |
| ORM | Entity Framework Core 10 (`Microsoft.EntityFrameworkCore.SqlServer`) |
| Background Jobs | Hangfire (`Hangfire.Core`, `Hangfire.SqlServer`, `Hangfire.AspNetCore`) |
| Messaging | Telegram Bot API (HTTPS Webhook) |
| Testing | xUnit (`xunit`, `Microsoft.NET.Test.Sdk`) |

---

## Getting Started (Local Development)

### 1. Prerequisites
- **.NET 10 SDK** (`10.0.403` or later)
- **Microsoft SQL Server** (Local default instance `localhost` or `MSSQLSERVER`, SQL Server Express, or LocalDB)

### 2. Build the Solution
```powershell
dotnet build OpsConnect.sln
```

### 3. Apply Database Migrations
The default development connection string in `src/OpsConnect.Api/appsettings.json` and `src/OpsConnect.Worker/appsettings.json` targets `Server=localhost;Database=OpsConnectDB;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True`.

To create or update `OpsConnectDB`:
```powershell
dotnet ef database update --project src/OpsConnect.Infrastructure --startup-project src/OpsConnect.Api
```

### 4. Run Automated Tests
```powershell
dotnet test OpsConnect.sln
```

---

## Documentation Index

- [Master Development Plan](DEVELOPMENT_PLAN.md)
- [Architecture Overview](docs/Architecture/Overview.md)
- [Database Schema & Design](docs/Database/SchemaDesign.md)
- [API Specification](docs/API/Endpoints.md)
- [Telegram Integration Guide](docs/Telegram/BotConfiguration.md)
- [Local Setup & Deployment Guide](docs/Deployment/LocalSetup.md)
