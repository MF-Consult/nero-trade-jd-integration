# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run Commands

```bash
# Build
dotnet build

# Build for release (as CI does)
dotnet build --configuration Release --output ./output

# Run locally (Azure Functions on port 8000)
dotnet run --project NeroTrade.JDIntegration
```

No automated test suite exists. `TestGeneratePdf` is a manual HTTP-triggered function for testing PDF generation.

## Architecture Overview

This is a **bidirectional data sync service** between two enterprise systems:
- **Uniconta** — Danish ERP system (source of truth for customers, items, orders)
- **JD Logistics** — external logistics/order management system

It runs as Azure Functions on short timer intervals (2–40 seconds) to keep both systems in sync.

### Data Flow

```
Uniconta ERP  ──►  SyncDebtors/Items/SalesOrders/PurchaseOrders  ──►  JD Logistics
Uniconta ERP  ◄──  SyncReceivedQuantity/RequestOrderStatus       ◄──  JD Logistics
```

PDF delivery notes are generated (QuestPDF) and uploaded to JD via pre-signed Azure Blob Storage URLs.

### Layer Structure

| Layer | Key Classes | Responsibility |
|-------|-------------|----------------|
| Functions | `Functions/` | Azure Function entry points, orchestration |
| Services | `Services/IJdLogisticsService`, `IUnicontaService`, `IDeliveryNotePdfService` | Business logic |
| Repositories | `Services/IJdRepository`, `IUnicontaRepository` | External API clients |
| Mappers | `Services/*Mapper` | Transform Uniconta models ↔ JD DTOs |
| Models | `Models/JD/`, `Models/Uniconta/`, `Models/Settings/` | DTOs and config |

### Key Infrastructure

- **UnicontaConnectionManager** — manages Uniconta SDK session/authentication (LiveAPI)
- **JdRepository** — HTTP REST client for JD API, uses Bearer token auth
- **UnicontaRepository** — uses Uniconta SDK (`QueryAPI`, `CrudAPI`) for ERP reads/writes
- **Program.cs** — DI registration for all services, settings binding

### Configuration (Environment Variables)

The app requires these settings (bound via `IOptions<T>`):

- `JdSettings__BaseUrl`, `JdSettings__BearerToken`, `JdSettings__TimeoutSeconds`, `JdSettings__DryRun`
- `UnicontaConfig__Username`, `UnicontaConfig__Password`, `UnicontaConfig__ApiKey`, `UnicontaConfig__CompanyId`
- `StatusMappingConfig__*` — maps JD status/stage codes to Uniconta group values

### Deployment

CI/CD via GitHub Actions (`.github/workflows/master_nero-trade-data-syncer.yml`):
- Triggers on push to `master`
- Deploys to Azure Function App: **nero-trade-data-syncer**
- Uses OpenID Connect federated identity (no stored secrets)

### Notable Patterns

- `DryRun` mode in `JdSettings` prevents writes to JD (log-only) — useful for testing
- Nullable reference types are enabled — null-check Uniconta query results carefully
- All sync functions are `TimerTrigger`-based except `TestGeneratePdf` (HTTP) and `SyncDebtorsToJd` (HTTP)
