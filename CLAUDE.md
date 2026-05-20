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

### Monitoring & remediation contract

**Canonical reference: [docs/operations.md](docs/operations.md)** — architecture, full schema, manual setup steps, endpoint catalogue, Hermes brain contract, multi-project reuse contract, file-by-file map. Read that file before changing anything in `Services/Logging/`, `Functions/Sync*`, or `Functions/Admin/`.

Quick rules for in-session edits:

- Every `LogAsync(new IntegrationLogEntry(...))` must set `CorrelationId = logScope.CorrelationId` so all rows from one invocation are linkable.
- For `error`/`warning` rows, also set `ErrorCode`, `Retryable`, and `SuggestedAction`. The Hermes agent uses these to decide auto-retry vs. ask-in-Slack.
- New `/admin/*` endpoints reuse existing `IUnicontaService` / `IJdLogisticsService` methods — never invent new business logic in `RemediationEndpoints.cs`.

**Error-code taxonomy** (`SUBSYSTEM_REASON`, SCREAMING_SNAKE — extend freely):

- `SYNC_RUN_FAILED` — top-level catch in a sync function; almost always retryable
- `JD_TIMEOUT`, `JD_AUTH_FAILED`, `JD_RATE_LIMITED`, `JD_5XX` — transport-level JD failures
- `JD_VALIDATION_REJECTED` — JD accepted the request but rejected the payload (not retryable; needs data fix)
- `JD_LOOKUP_MISS` — JD returned 404 for a record we expected
- `UNICONTA_DUPLICATE_SO`, `UNICONTA_LOOKUP_MISS`, `UNICONTA_CRUD_FAILED`, `UNICONTA_AUTH_FAILED` — Uniconta-side
- `SHIPMONDO_NO_CARRIER`, `SHIPMONDO_INVALID_POSTAL` — Shipmondo carrier-mapping problems
- `PDF_GENERATION_FAILED`, `BLOB_UPLOAD_FAILED` — delivery-note pipeline
- `MAPPER_NULL_FIELD`, `MAPPER_INVALID_STATE` — code-level edge cases in mappers
- `UNKNOWN` — fallback; these go to the Hermes "novel error" cascade
