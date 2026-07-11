# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## ⚠️ Field-mapping contract (read first)

**[docs/field-mapping.md](docs/field-mapping.md) is the authoritative source of truth for every
Uniconta ↔ JD field translation (both directions, both order types).**

- It **must ALWAYS be respected.** Never change a mapping (rename a source field, alter a transform,
  add/drop a JD field) unless the user **explicitly asks for that specific change** in the session.
- **Every mapping change in code MUST be reflected in `docs/field-mapping.md` in the same change**,
  with a dated entry in its Changelog. Code and that doc must never diverge.
- Before editing `SalesOrderMapper.cs`, `PurchaseOrderMapper.cs`, the order-sourcing in
  `UnicontaRepository.cs`, or `UnicontaUserFields.cs`, read that doc and keep it in sync.

## Build & Run Commands

```bash
# Build
dotnet build

# Build for release (matches what CI does for deploy — function project only, not the .sln)
dotnet build NeroTrade.JDIntegration/NeroTrade.JDIntegration.csproj --configuration Release --output ./output

# Run unit tests
dotnet test NeroTrade.JDIntegration.Tests/NeroTrade.JDIntegration.Tests.csproj
```

### Running the Functions host locally (gotcha)

`dotnet run --project NeroTrade.JDIntegration` **does not work on this machine**: the isolated host
delegates to `func`, and the npm `func` shim has no `func.exe`, so the launch fails with
*"error trying to start process 'func' … file not found."* Use one of:

```bash
# Azure Functions Core Tools directly (port 8000)
func host start --port 8000 --dotnet-isolated    # run from the NeroTrade.JDIntegration/ project dir
```

…or just press **F5 in Visual Studio** (its launch profile already runs the host on 8000).

**Build-lock note:** while a VS debug session is running the host, `bin\Debug\net9.0\NeroTrade.JDIntegration.dll`
is locked, so `dotnet build`/`dotnet test` fail at the copy step. Either stop debugging (Shift+F5), or
build/test to a temp output to verify without disturbing it:
`dotnet test … -p:BaseOutputPath=$env:TEMP\jdverify\`.

Unit tests live in `NeroTrade.JDIntegration.Tests/`. They pin the JD repository / cache / classifier contracts that recent prod incidents have depended on (cache poisoning, request-message reuse, error-code mapping). CI runs them on every PR (`.github/workflows/pr-build-and-test.yml`) and again before deploy on master. `TestGeneratePdf` is still a manual HTTP-triggered function for ad-hoc PDF testing.

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

- `DryRun` mode (`JD__DryRun`) is the integration-wide "mutate nothing" switch — it blocks **both**
  mutating JD calls (`JdRepository.SendWithRetryAsync`) **and** Uniconta writes (`UnicontaService`),
  so a dry run is fully side-effect free. Used for safe payload previews — see [docs/operations.md §10](docs/operations.md). `GET` reads still run, so payloads build in full.
- Sales-order eligibility: `xTransferToJD` = true, `Group` empty/`Fejlet`, and `UpdatedAt` within the
  last **1 day** (`SalesOrderRecentWindow`). Purchase orders have no time window (all scanned).
- Nullable reference types are enabled — null-check Uniconta query results carefully
- All sync functions are `TimerTrigger`-based except `TestGeneratePdf` (HTTP) and `SyncDebtorsToJd` (HTTP)
- **Sync cadence is config-driven, NOT the `TimerTrigger` cron.** Each sync's `[TimerTrigger(...)]` is
  only a fast heartbeat; `SyncScheduler` (config: `SyncScheduling`) gates each tick down to a per-job
  day/night interval and returns before any Uniconta call on non-due ticks. This keeps Uniconta volume
  under budget (~<4.000 calls/day) and lets ops retune without a redeploy. `MaxSessionAge` in
  `UnicontaConnectionManager` is likewise day/night-aware (short by day for UI-edit freshness, relaxed
  at night). **In-memory last-run state assumes a single worker instance** — pin `WEBSITE_MAX_INSTANCES=1`.
  To change how often a sync runs, edit `SyncScheduling` config — do not touch the heartbeat cron.

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
- `UNICONTA_DUPLICATE_SO`, `UNICONTA_LOOKUP_MISS`, `UNICONTA_CRUD_FAILED`, `UNICONTA_ORDER_STATUS_FAILED`, `UNICONTA_AUTH_FAILED` — Uniconta-side. `UNICONTA_ORDER_STATUS_FAILED` is for sales-order Group/user-field update failures specifically (Created/Fejlet on push, JD→Uniconta status sync); other Uniconta writes keep `UNICONTA_CRUD_FAILED`.
- `SHIPMONDO_NO_CARRIER`, `SHIPMONDO_INVALID_POSTAL` — Shipmondo carrier-mapping problems
- `PDF_GENERATION_FAILED`, `BLOB_UPLOAD_FAILED` — delivery-note pipeline
- `MAPPER_NULL_FIELD`, `MAPPER_INVALID_STATE` — code-level edge cases in mappers
- `UNKNOWN` — fallback; these go to the Hermes "novel error" cascade
