# OpsConnect — Local Setup & Deployment Guide

## 1. Local Development Environment

### Installed Software Verified in Phase 1
- **.NET SDK**: `10.0.403` (`net10.0`)
- **SQL Server**: Local SQL Server default instance (`localhost` / `MSSQLSERVER`) and `MSSQLLocalDB`
- **IDE**: Visual Studio 2022 / Visual Studio 2026

---

## 2. Database Configuration

The connection string `OpsConnectDb` is configured in `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "OpsConnectDb": "Server=localhost;Database=OpsConnectDB;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True"
  }
}
```

### Applying EF Core Migrations
From the repository root (`d:\OPSConnect`):

```powershell
dotnet ef database update --project src/OpsConnect.Infrastructure --startup-project src/OpsConnect.Api
```

---

## 3. Managing Secrets Locally

Use `.NET User Secrets` for `OpsConnect.Api` and `OpsConnect.Worker` so sensitive tokens are never committed to Git:

```powershell
dotnet user-secrets init --project src/OpsConnect.Api
dotnet user-secrets set "Telegram:BotToken" "<YOUR_TELEGRAM_BOT_TOKEN>" --project src/OpsConnect.Api
dotnet user-secrets set "Telegram:WebhookSecretToken" "<YOUR_SECRET_TOKEN>" --project src/OpsConnect.Api
```
