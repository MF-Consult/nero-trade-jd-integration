# Monitoring & Proactive Error Correction — Operations Reference

Single source of truth for the monitoring + remediation substrate of this integration. Covers the **why**, the **architecture**, the **code surface**, the **manual setup** that must happen outside the repo, the **agent contract**, and the **reuse contract** for future projects on the same Monitoring Subscription.

> Last updated: 2026-05-20 · Phases 1 + 2 complete in this repo · Phase 3 is Hermes-side.

---

## 1. Why this exists

A Monitoring Subscription has been sold for the Nero Trade ↔ JD Logistics integration. It promises two things:

1. **Visibility** into integration health — every failure is visible in seconds, not days.
2. **Proactive remediation** — known failures self-heal without human babysitting; only the long tail escalates.

The system is designed so the **same substrate works for every future project** (other Azure Functions, n8n flows, etc.). One Hermes agent runtime serves them all. The substrate is the product.

---

## 2. Architecture

```
┌──────────────────────────────┐        ┌──────────────────┐        ┌──────────────────┐
│ Nero Trade JD Integration    │        │     Supabase     │        │  Hermes Agents   │
│   (this Azure Functions app) │ writes │ integration_logs │ webhook│   (user's server)│
│                              ├───────►│ + playbooks      ├───────►│                  │
│  Layer 1: rich events        │        │                  │        │ classifier →     │
│  Layer 2: action endpoints   │◄───────│                  │◄───────│ playbook lookup→ │
│   (POST /remediation/* secret-     │ calls  │                  │ writes │ act OR ask Slack │
│    gated)                    │        │                  │ status │                  │
└──────────────┬───────────────┘        └──────────────────┘        └────────┬─────────┘
               │                                                             │
               │ infra/crash telemetry (built-in)                            ▼
               ▼                                                  ┌──────────────────────┐
       ┌────────────────────┐  Slack webhook on alerts            │ Slack channel        │
       │ Application        ├───────────────────────────────────► │ #nero-trade-jd-alerts│
       │ Insights           │                                     │ approve / reject     │
       │ (Azure-native)     │                                     └──────────────────────┘
       └────────────────────┘
```

Two observability planes with distinct jobs:

| Plane                           | Owns                                                                 |
|---------------------------------|----------------------------------------------------------------------|
| **`integration_logs` (Supabase)** | Business events — "did the integration do its job?" Agent's substrate. |
| **Application Insights**        | Platform telemetry — "is the host alive?" Catches the cases the in-process logger can't (unhandled exceptions, missed timer triggers, function failure rate). |

---

## 3. Status at a glance

| Phase     | Scope                                                                                                                                          | Status                |
|-----------|------------------------------------------------------------------------------------------------------------------------------------------------|-----------------------|
| **1**     | Rich events (correlation_id, error_code, retryable, suggested_action) plumbed through all Sync* functions. Supabase webhook + App Insights alerts wired manually. | ✅ Code done — manual setup pending |
| **2**     | `/remediation/*` remediation endpoints + `integration_playbooks` table seeded. Hermes-side Slack approval buttons.                                   | ✅ Code done — Hermes-side pending |
| **3**     | Hermes classifier + auto-approve per error_code. No further repo changes.                                                                      | Hermes-side work      |
| **4 (later)** | Agent opens GitHub PRs for repeating code-level bug patterns.                                                                              | Deferred              |

---

## 4. Manual one-time setup (outside the repo)

Three things must happen in external systems before the loop is live end-to-end.

### 4.1 Set the Function App's remediation secret

Azure Portal → Function App `nero-trade-data-syncer` → **Configuration → Application settings** → Add:

| Name                          | Value                                              |
|-------------------------------|----------------------------------------------------|
| `Remediation__SharedSecret`   | Long random string (≥ 32 chars). Save in a vault. |

The `/remediation/*` endpoints **refuse to run** until this is set, so the surface cannot go live unauthenticated.

Hermes needs two things to call the endpoints:
- The Azure Functions key (`x-functions-key` header).
- This shared secret (`X-Remediation-Secret` header).

### 4.2 Supabase Database Webhook → Hermes

Supabase Dashboard → project **MF Consult Integration Logging** (`fmuxfpdjmdypxymhdgme`) → **Database → Webhooks → Create a new hook**.

| Field        | Value                                                                            |
|--------------|----------------------------------------------------------------------------------|
| Name         | `hermes_integration_errors`                                                      |
| Table        | `public.integration_logs`                                                        |
| Events       | `INSERT` only                                                                    |
| Type         | HTTP Request                                                                     |
| URL          | `https://<hermes-host>/ingest/integration-errors`                                |
| Method       | POST                                                                             |
| Headers      | `Authorization: Bearer <HERMES_INGEST_TOKEN>`, `Content-Type: application/json`  |
| Filter       | `level=eq.error`                                                                 |

The webhook payload includes the full row (correlation_id, error_code, payload, suggested_action, …) — no extra round-trip needed for Hermes to start reasoning.

### 4.3 App Insights alert rules

Azure Portal → Function App `nero-trade-data-syncer` → **Application Insights → Alerts → New alert rule**.

Three baseline rules → all share one Action Group that posts to `#nero-trade-jd-alerts` via Slack webhook.

| Alert                                  | Signal                                                                                  | Threshold                       |
|----------------------------------------|-----------------------------------------------------------------------------------------|---------------------------------|
| Function failure rate                  | `requests \| where success == false \| summarize count() by bin(timestamp, 5m)`         | > 10 in 5 min                   |
| Unhandled exception spike              | `exceptions \| summarize count() by bin(timestamp, 5m)`                                 | > 5 in 5 min                    |
| Timer trigger silent (no invocations)  | `traces \| where message has "started" \| summarize count() by bin(timestamp, 10m)`     | == 0 for 15 min on any Sync*    |

Create the Slack Action Group once: **Monitor → Action groups → New** → action type **Webhook** → Slack incoming webhook URL. Reuse it across the three alert rules.

### 4.4 Uniconta user fields (sales + purchase orders)

These user-defined fields must exist in Uniconta (company **129192**) with the **exact** names below before the
integration runs. The source of truth is `Services/UnicontaHandler/Constants/UnicontaUserFields.cs`; the sales-order
subset is mirrored in the plugin's `PluginFieldNames.cs` (pinned by `PluginFieldNamesTests`). Fail-open: a missing field
never crashes the integration, but the corresponding mapping/validation silently does nothing.

**Format** = Uniconta field type. **Value-list order matters** — the plugin maps a stored index back to its text via the
fixed order shown (`UserFieldValueNormalizer`); create the list entries in that order.

#### Sales order (`DebtorOrder`)

User-filled:

| Field | Format | Values / note | Required when `xTransferToJD` = Ja |
|---|---|---|---|
| `xTransferToJD` | Checkbox (bool) | Opt-in: transfer the order to JD | — (the trigger itself) |
| `xTransportTypes` | Value list | `JD Logistik Transport` (0), `Ekstern Transport` (1), `Afhenter Selv` (2) — **in that order** | ✅ always |
| `xDeliveryType` | Value list | `GLS` (0), `Palle Fragt` (1) — **in that order** | ✅ only when transport = JD Logistik (hidden otherwise) |
| `xByttepaller` | Value list | `Ja` (0), `Nej` (1) — **no default (blank)** | ✅ only for pallet orders (hidden otherwise) |
| `xTrackingNote` | Text | Tracking note → JD internal note | ✅ always |
| `xTrackingNoteOnLabel` | Text | Note on delivery label | no |
| `xRemarksForJD` | Text | Remark → appended to JD internal note | no |
| `xTimeForDelivery` | Date/time | Only the time-of-day is used (timed delivery) | no |
| `xMessageForTransport` | Text | Message to carrier (`carrierInstructions`) | no |
| Delivery date (`DeliveryDate`, built-in) | Date | Standard field — must be filled | ✅ always |

Integration-written (must **exist**, never filled manually):

| Field | Format | Written by |
|---|---|---|
| `xJDOrderId` | Text | JD's request-order id, written back on create |
| `xIntegrationIssue` | Text | Failure reason on JD reject (cleared on success) |
| Order group (`Group`, built-in) | Value list | Set to `Oprettet` / `Fejlet`, then the live JD status |

#### Purchase order (`CreditorOrder`)

User-filled:

| Field | Format | Values / note | Maps to JD |
|---|---|---|---|
| `xTransferToJD` | Checkbox (bool) | Opt-in: transfer to JD | trigger |
| `xCarrier` | Text | **Speditør** (freight forwarder) | → `carrier` |
| `xRemarksForJD` | Text | **Bemærkninger** (manpower needs etc.) | → `text` (after `PO {n}`) |
| `xEnhedstype` | Value list | `Palle`, `Container` — **names must match JD's container types exactly** | → parent line `inventoryContainerType` |
| `xAntalEnheder` | Number | Count of pallets/containers | → parent line `quantity` |
| Delivery date (`DeliveryDate`, built-in) | Date | Expected delivery date | → `date` |

> The Lagerhotel fields (`xEnhedstype` + `xAntalEnheder`) drive the JD parent/child structure: when both are set the
> shipment gets a pure-container parent line (`isSubItem=false`, no SKU) with the product lines as children
> (`isSubItem=true`); when unset the products go as a flat list. Group `xCarrier` / `xRemarksForJD` / `xEnhedstype` /
> `xAntalEnheder` under a "Lagerhotel" field group in the UI.

Integration-written:

| Field | Format | Values |
|---|---|---|
| `xJDStatus` | Value list | `Oprettet`, `Manuel handling`, `Færdigbehandlet` — blank / `Manuel handling` = pending |

Purchase-order **line** (`CreditorOrderLine`):

| Field | Format | Note |
|---|---|---|
| `xExternalSku` | Text | Customer item number → line `externalIdentification` |

**Manual-only (NOT read or written by the integration):**

| Field | Format | Note |
|---|---|---|
| `xFixedIssuesJD` | Checkbox (bool) | Staff tick it once they have fixed an issue on a PO line. Purely a visual workflow flag — the integration never reads or sets it. Re-sending a parked PO still happens via `xTransferToJD` (see §7.2). Documented here so it is not mistaken for a missing mapping. |

#### Item & debtor transfer flags (for completeness)

| Table | Field | Format |
|---|---|---|
| Item (`InvItem`) | `XoverforVare` | Checkbox (bool) |
| Item (`InvItem`) | `xExternalSku` | Text (external SKU) |
| Debtor (`Debtor`) | `Xoverfort` | Checkbox (bool) |

### 4.5 Sync cadence & scaling (`SyncScheduling`)

The five `Sync*` timer functions no longer set their real frequency in the `[TimerTrigger(...)]` cron —
that cron is only a fast heartbeat (30 s for Sales/PO, 60 s for the rest). The actual per-job day/night
cadence lives in the `SyncScheduling` config section and is enforced by `SyncScheduler`, which returns
before any Uniconta call on a non-due tick. This keeps Uniconta volume under the ~4.000 calls/day target
and lets you retune without a redeploy.

Defaults (see `Models/Settings/SyncSchedulingOptions.cs`) — day window **07–22 local**:

| Job (config key) | Day | Night |
|---|---|---|
| `SalesOrders` | 50 s | 5 min |
| `PurchaseOrders` | 72 s | 30 min |
| `PostedPurchaseInvoices` | 5 min | 30 min |
| `RequestOrderStatus` | 5 min | 30 min |
| `Items` | 3 min | 60 min |
| `ReceivedQuantity` | 15 min | 60 min |
| Uniconta session max age | 90 s | 15 min |

Config keys (App Settings use `SyncScheduling__…`): `TimeZoneId` (default `Romance Standard Time`),
`DayStartHour`, `DayEndHour`, `SessionMaxAgeDaySeconds`, `SessionMaxAgeNightSeconds`, and
`Jobs__<JobName>__DaySeconds` / `Jobs__<JobName>__NightSeconds`.

**Caveats:**
- **`WEBSITE_TIME_ZONE` is NOT needed** — `SyncScheduler` converts UTC→local itself via `TimeZoneId`,
  and the heartbeat crons are timezone-agnostic.
- **Pin to a single instance** (`WEBSITE_MAX_INSTANCES=1` / functionAppScaleLimit=1). Last-run state is
  in-memory per worker; a scale-out would give each instance its own schedule and multiply the calls.
- **Effective cadence is quantized to the heartbeat** (30 s → Sales ~60 s, PO ~90 s). Always ≥ the
  configured value, so always ≤ budget. For exact sub-minute values, lower the heartbeat cron.

---

## 5. `integration_logs` schema reference

Existing columns retained; **bold** columns added in Phase 1.

| Column            | Type         | Purpose                                                                              |
|-------------------|--------------|--------------------------------------------------------------------------------------|
| id                | bigint       | PK                                                                                   |
| created_at        | timestamptz  | row insert time                                                                      |
| integration_name  | text         | e.g. `nero-trade-jd-integration`                                                     |
| level             | text         | `info` / `warning` / `error`                                                         |
| source_system     | text         | `Uniconta` / `JD` / `Integration`                                                    |
| external_id       | text         | order number, sku, etc.                                                              |
| message           | text         | human-readable                                                                       |
| stack_trace       | text         | only on `error` from a catch                                                         |
| payload           | jsonb        | agent-readable rich-context blob (request, response, snapshot, summary metrics)      |
| slack_thread_ts   | text         | filled by Hermes when it opens a Slack thread for the incident                       |
| **error_code**    | text         | stable taxonomy — see §6                                                             |
| **correlation_id**| uuid         | shared across all rows from one function invocation                                  |
| **retryable**     | boolean      | hint to the agent: auto-retry or escalate?                                           |
| **attempt**       | integer      | reserved — Phase 2 doesn't yet increment this                                        |
| **suggested_action** | text      | free-text hint from the catch block                                                  |
| **status**        | text         | `open` / `ack` / `auto_fixed` / `resolved` / `wontfix` — CHECK-constrained, default `open` |
| **resolution**    | jsonb        | what the agent (or `IntegrationRun` auto-resolve) did and when                       |
| **project**       | text         | default `nero-trade-jd-integration` — multi-project routing key                      |
| **duration_ms**   | integer      | set only on the run-completion row written by `IntegrationRun`; NULL on per-event rows |

Indexes: `(status, level)`, `correlation_id`, `project`, partial on `error_code`, partial `(project, integration_name, external_id, status) where status in ('open','ack')` (used by auto-resolve PATCH).

### 5.1 Run tracking (`IntegrationRun`)

Every timer-driven sync function is wrapped:

```csharp
await using var run = integrationLogger.BeginRun("SyncSalesOrdersToJd", cancellationToken);
var logScope = run.Scope;
// ... existing body unchanged; existing IntegrationLogEntry call sites keep CorrelationId = logScope.CorrelationId ...
catch (Exception ex) { run.MarkFailed(ex); /* existing emit + throw */ }
```

The wrapper writes:

1. A `started` info row at entry (payload: `{ run_name, started_at }`).
2. A paired `completed` row in `DisposeAsync` (in a `try/finally`, so an exception still produces a completion row at `level=error` with `duration_ms` set). Payload: `{ run_name, started_at, finished_at, duration_ms, counts: <whatever caller attached via AttachCompletionPayload> }`.

The existing per-function completion `IntegrationLogEntry` rows are kept unchanged. The two rows aggregate fine in `integration_runs`.

### 5.2 Views

- **`public.integration_runs`** — one row per `correlation_id`: `run_name`, `started_at`, `finished_at`, `duration_ms`, `processed`/`succeeded`/`failed` (from `payload.counts`), `run_status` (ok/warning/error from `bool_or` over levels), `log_count`. Rows without `correlation_id` (older data) are filtered out.
- **`public.integration_order_timeline`** — every row that carries an `external_id`, with `created_at`, `level`, `error_code`, `message`, `status`, `correlation_id`, `log_id`. Order by `external_id, created_at` when querying.

### 5.3 Auto-resolve

`IIntegrationLogger.MarkResolvedAsync(integrationName, externalId, successCorrelationId, ct)` is called immediately after every per-order success row (`SyncSalesOrdersToJd`, `SyncPurchaseOrdersToJd`, `SyncRequestOrderStatusToUniconta`, `SyncReceivedQuantityToUniconta`). It issues:

```http
PATCH /rest/v1/integration_logs?project=eq.<P>&integration_name=eq.<I>&external_id=eq.<E>&status=in.(open,ack)
{ "status": "auto_fixed", "resolution": { "resolved_by": "integration-run-auto-resolve", "resolved_at": "...", "success_correlation_id": "..." } }
```

Scope is `(project, integration_name, external_id, status in open|ack)` — never touches `wontfix` / `resolved`, and reused `external_id` across integrations cannot match. Failures from the PATCH never break the main flow (logged to App Insights).

> **Heads-up:** RLS is currently disabled on `integration_logs` (pre-existing). Acceptable because all reads/writes use the service-role key. Before exposing the substrate to projects that use weaker keys, enable RLS and add a permissive service-role policy.

---

## 6. `error_code` taxonomy

Convention: `SUBSYSTEM_REASON`, SCREAMING_SNAKE. Extend freely — new codes don't require a migration.

| Code                          | Meaning                                                                                 |
|-------------------------------|-----------------------------------------------------------------------------------------|
| `SYNC_RUN_FAILED`             | Top-level catch in a sync function. Almost always retryable.                            |
| `JD_TIMEOUT`                  | Network-level timeout calling JD.                                                       |
| `JD_AUTH_FAILED`              | JD rejected the bearer token.                                                           |
| `JD_RATE_LIMITED`             | JD returned 429.                                                                        |
| `JD_5XX`                      | JD returned a server error.                                                             |
| `JD_VALIDATION_REJECTED`      | JD accepted the request but rejected the payload. **Not retryable** — data fix needed.  |
| `JD_LOOKUP_MISS`              | JD returned 404 for an expected record.                                                 |
| `JD_CONTAINER_TYPE_UNMAPPED`  | An incoming-shipment line's unit matched no JD container type; the line shipped as `Stk`. **Not retryable** — add the translation in `UnitTranslator` or the container type in JD. |
| `UNICONTA_CONNECT_FAILED`     | Uniconta unreachable for one tick — nearly always `OpenCompany` returning no company for a valid id. Written at **warning** level (so it never reaches the `level=eq.error` Hermes webhook) and the sync returns instead of throwing. No action unless it persists across many consecutive ticks. |
| `UNICONTA_NO_STOCK_LINES`     | A posted purchase invoice is flagged for JD but has no `Stock` lines, so the safety-net skips it every tick. **Not retryable.** Either the invoice really is fee-only, or it is the stale-read symptom (see CLAUDE.md § "Uniconta reads"). Rate-limited to one row per order per hour. |
| `UNICONTA_DUPLICATE_SO`       | Multiple SOs collide on the same number.                                                |
| `UNICONTA_LOOKUP_MISS`        | Expected Uniconta record absent.                                                        |
| `UNICONTA_CRUD_FAILED`        | Generic Uniconta write rejected (usually transient). Use the more specific codes below when they apply. |
| `UNICONTA_ORDER_STATUS_FAILED`| Sales-order Group/user-field update failed (mark-as-Created, mark-as-Fejlet, or JD→Uniconta status sync). Specific subtype of CRUD_FAILED so order-status incidents don't collide with debtor/contact/PO upsert failures. |
| `UNICONTA_AUTH_FAILED`        | Uniconta session/credentials issue.                                                     |
| `SHIPMONDO_NO_CARRIER`        | No carrier mapping found for an order's postal code.                                    |
| `SHIPMONDO_INVALID_POSTAL`    | Postal code is malformed/unknown.                                                       |
| `PDF_GENERATION_FAILED`       | QuestPDF threw producing a delivery note.                                               |
| `BLOB_UPLOAD_FAILED`          | Pre-signed Azure Blob upload failed.                                                    |
| `MAPPER_NULL_FIELD`           | Mapper hit an unexpected null on a required field.                                      |
| `MAPPER_INVALID_STATE`        | Mapper saw an entity in a shape the code didn't expect.                                 |
| `REMEDIATION_APPLIED`         | Audit row written by `/remediation/*` after a successful remediation.                         |
| `REMEDIATION_NOOP`            | `/remediation/*` returned a non-success — the underlying call didn't apply.                   |
| `REMEDIATION_FAILED`          | `/remediation/*` threw before completing.                                                     |
| `UNKNOWN`                     | Fallback. These go to the Hermes "novel error" reasoning cascade.                       |

---

## 7. `/remediation/*` remediation endpoints

All endpoints live in `Functions/Admin/RemediationEndpoints.cs` and require two layers of auth:

1. **Azure Functions key** — `AuthorizationLevel.Function`, sent as `x-functions-key` header or `?code=` query string.
2. **Shared secret** — `X-Remediation-Secret` header, value bound from `Remediation:SharedSecret` config setting.

Every successful call writes an audit row to `integration_logs` with `error_code = REMEDIATION_APPLIED` and the action details in `payload`. Failures write `REMEDIATION_NOOP`. Unhandled throws write `REMEDIATION_FAILED`.

Constant-time comparison is used for the shared secret to avoid leaking length via early exit.

> **Never route these under `admin/`.** The Functions host reserves `/admin/*` for its own management API
> (`/admin/host/status`, `/admin/functions/...`). These three endpoints originally used `admin/` and
> therefore **failed to register at every host start** — "The specified route conflicts with one or more
> built in routes" — from 2026-06-27 until 2026-07-27 (8.784 log rows per endpoint). They were never
> callable in production, and nothing surfaced it because the host logs the conflict at startup only.
> Renamed to `remediation/` on 2026-07-27. If Hermes has the old URLs stored anywhere, update them.

### 7.1 `POST /remediation/retry-sales-order/{soNumber}`

Sets the SO's `Group` back to empty + clears `xIntegrationIssue` + flips `xTransferToJD` back to `true`. Next 30-second `SyncSalesOrdersToJd` tick picks it up and re-pushes to JD.

```http
POST https://<host>/api/remediation/retry-sales-order/2135
x-functions-key: <function-key>
X-Remediation-Secret: <shared-secret>
```

```json
200 OK
{ "action": "retry-sales-order", "soNumber": 2135, "applied": true }
```

### 7.2 `POST /remediation/retry-purchase-order/{poNumber}`

Sets `xJDStatus = "Manuel handling"` + flips `xTransferToJD` back to `true`. Next 40-second `SyncPurchaseOrdersToJd` tick re-pushes.

```http
POST https://<host>/api/remediation/retry-purchase-order/4711
x-functions-key: <function-key>
X-Remediation-Secret: <shared-secret>
```

```json
200 OK
{ "action": "retry-purchase-order", "poNumber": 4711, "applied": true }
```

### 7.3 `POST /remediation/override-order-status/{orderNumber}`

Forces a sales order's `Group` to a specific value when JD/Uniconta status drifted apart and a tick won't fix it.

```http
POST https://<host>/api/remediation/override-order-status/2135
Content-Type: application/json
x-functions-key: <function-key>
X-Remediation-Secret: <shared-secret>

{ "group": "Godkendt" }
```

```json
200 OK
{ "action": "override-order-status", "orderNumber": 2135, "group": "Godkendt", "applied": true }
```

### 7.4 Adding new endpoints

Reuse an existing `IUnicontaService` / `IJdLogisticsService` method — **never invent new business logic** in `RemediationEndpoints.cs`. The pattern is:

1. Validate header (`HandleAsync` does this for you).
2. Call the service.
3. Write `REMEDIATION_APPLIED` audit row via `WriteAuditAsync`.
4. Return JSON envelope.

See the three existing endpoints as templates.

### 7.5 Read-only inspection endpoints (Uniconta source of truth)

Two **GET**, **read-only** endpoints for answering "why did this order map like that?" (e.g. speditør/kolli). They pull a specific order straight from Uniconta — **ignoring** the transfer-flag/status eligibility the sync enforces — and return BOTH the raw Uniconta projection and the exact JD payload the mappers produce. No mutation, so safe regardless of DryRun. Function-key auth only (no shared secret). Code: `Functions/InspectOrderFromUniconta.cs`.

```http
GET https://<host>/api/inspect/purchase-order/{poNumber}
x-functions-key: <function-key>
```

- Tries the **open** purchase order first; if the order was booked (bogført) it falls back to the **posted invoice** (the safety-net source) and reports which via `source: "open-order" | "posted-invoice"`.
- The `jd` block spells out the `[JsonIgnore]` fields (`Sku`, `unit`, `SourcePurchaseNumber`) that never reach JD's wire payload, so carrier/kolli/catalog are fully visible.

```json
200 OK
{
  "poNumber": 39,
  "source": "posted-invoice",
  "uniconta": { "purchaseNumber": 39, "carrier": "...", "containerType": "...", "containerCount": 0, "lines": [ ... ] },
  "jd": { "text": "PO 39", "carrier": "...", "lines": [ { "isSubItem": false, "quantity": 1, "sku": "...", "unit": "...", "externalIdentification": null } ] }
}
```

```http
GET https://<host>/api/inspect/sales-order/{soNumber}
x-functions-key: <function-key>
```

Returns `{ soNumber, uniconta: <LocalSalesOrder>, jd: <JdRequestOrderCreate> }`. Uses the same `ProjectSalesOrder` projection as the sync path, so what you see is what the sync would map.

---

## 8. `integration_playbooks` schema

Hermes reads + writes here as it learns. One row per `(project, error_code, pattern_signature)` (unique).

| Column            | Type        | Purpose                                                                                       |
|-------------------|-------------|-----------------------------------------------------------------------------------------------|
| id                | bigserial   | PK                                                                                            |
| project           | text        | `nero-trade-jd-integration` etc. Multi-project routing.                                       |
| error_code        | text        | Matches `integration_logs.error_code`.                                                        |
| pattern_signature | text        | Optional sub-key (e.g. distinguish two flavors of the same error code by message regex).      |
| description       | text        | Human-readable summary used in Slack messages.                                                |
| remediation       | jsonb       | Shape: `wait_and_observe`, `call_endpoint`, `escalate_to_slack`. Free-form within those.      |
| auto_approve      | boolean     | When `true`, agent applies silently. **Earned, not given.**                                    |
| success_count     | integer     | Increments after successful applications. Promotion gate for `auto_approve`.                  |
| failure_count     | integer     | Increments when remediation didn't actually fix the underlying error within N ticks.          |
| last_used_at      | timestamptz | Surfaces stale playbooks for review.                                                          |
| created_at        | timestamptz | Audit                                                                                         |
| updated_at        | timestamptz | Audit                                                                                         |

### Seed playbooks (already inserted)

| error_code                | remediation                                                | auto_approve |
|---------------------------|------------------------------------------------------------|--------------|
| `UNICONTA_CRUD_FAILED`    | `wait_and_observe`, 60s — self-heals on next tick          | true         |
| `JD_LOOKUP_MISS`          | `wait_and_observe`, 300s — likely propagation lag          | true         |
| `SYNC_RUN_FAILED`         | `wait_and_observe`, 60s; escalate after 3 attempts         | true         |
| `JD_VALIDATION_REJECTED`  | `escalate_to_slack` — needs human review of source data    | false        |

The agent learns the rest — new playbooks get inserted on first approved remediation of a novel error.

---

## 9. Hermes brain contract (what the agent does)

This section describes the agent-side behavior the repo is built to support. Implementation lives on the Hermes server, not here.

When a row arrives via the Supabase webhook:

1. **Look up `integration_playbooks`** by `(project, error_code, pattern_signature)`.
2. **Match + `auto_approve = true`** → call the `remediation.endpoint` with the shared secret and function key. On 200, update the source `integration_logs` row: `status = auto_fixed`, `resolution = { applied_at, endpoint, response }`. Done. **No Slack noise.**
3. **Match + `auto_approve = false`** → post Slack message to `#nero-trade-jd-alerts`:

    ```
    🟠 nero-trade-jd-integration · UNICONTA_CRUD_FAILED · SO 2135
    Suggested: POST /remediation/retry-sales-order/2135
    [ Approve ] [ Reject ] [ Snooze 1h ]
    ```

    - **Approve** → call endpoint → update source row → reply in thread with result.
    - **Reject** → update source row `status = wontfix`, `resolution = { rejected_by, rejected_at, reason }`.
    - **Snooze** → re-queue the event for re-evaluation after the snooze window.

4. **No match (novel error)** → agent reasons over `payload` + sibling rows from same `correlation_id` (one query: `WHERE correlation_id = X`) → drafts a remediation → posts as Slack message (same shape as above) → on first approval, **insert a new `integration_playbooks` row** with `auto_approve = false`. Over time, after N successful applications, promote to `auto_approve = true` (manually for now, possibly automated later).

### Slack message contract

- One **dedicated channel per project** (`#nero-trade-jd-alerts` for this integration).
- The agent writes `integration_logs.slack_thread_ts` after posting, so subsequent rows on the same incident reply in the same thread instead of spamming the channel.
- The `correlation_id` is the key for thread grouping.

---

## 10. End-to-end verification

### Phase 1 — visibility

1. Trigger a forced error in any `Sync*` function (e.g. point `JdSettings:BaseUrl` at a bogus URL locally).
2. Confirm a row appears in `integration_logs` with:
    - `level = error`
    - `error_code = SYNC_RUN_FAILED`
    - `correlation_id` populated
    - `retryable = true`
    - `suggested_action` populated
    - `payload` containing context
3. Confirm a Slack alert posts to `#nero-trade-jd-alerts` (after §4.2 webhook is configured).

### Phase 2 — one-click remediation

1. With the Supabase webhook firing, an error event reaches Hermes.
2. Hermes posts a Slack message with Approve/Reject buttons.
3. Click **Approve** → Hermes calls `/remediation/retry-sales-order/2135` with both headers.
4. Confirm:
    - Endpoint returns 200 with `applied = true`.
    - A new `integration_logs` row appears with `error_code = REMEDIATION_APPLIED`, `level = info`, `status = open`.
    - The original error row's `status` flips to `auto_fixed` (Hermes does this — verify via Supabase Dashboard).
    - The next sync tick re-pushes the SO to JD successfully.

### Phase 3 — proactive auto-fix

When live:
- A repeated `UNICONTA_CRUD_FAILED` never reaches Slack — auto-resolves silently via the seed playbook.
- A novel error escalates to Slack on first occurrence, then auto-resolves on subsequent ones (assuming first remediation was approved).

### Dry-run: safe payload preview

`JdSettings.DryRun` lets you exercise a full sync against **real Uniconta data** while mutating **nothing** in either system — no calls reach JD and no status fields are written back to Uniconta. Used to inspect the exact payload an order would produce (e.g. the purchase-order container parent/child structure, carrier, text) before going live.

How it works: every mutating JD call (`POST` / `PUT` / `PATCH` / `DELETE`) is intercepted in `JdRepository.SendWithRetryAsync`. With `DryRun = true` the call is logged and skipped, returning a synthetic `200 OK`; **`GET` reads still execute**, so catalog / container-type lookups and dedup run normally and the payload is built in full. A dry run therefore mutates nothing in JD (this includes the manual `DeleteSalesOrderFromJd` — turn DryRun off for a real deletion).

Uniconta writes are guarded the same way: every mutating method on `UnicontaService` (sales-order status, purchase-order header/line fields) short-circuits under `DryRun`, logging `[DRY-RUN] Skipping Uniconta write …` and returning success without touching Uniconta. **Added 2026-06-17** — before this fix `DryRun` only blocked JD, so a dry run still wrote order status back to Uniconta and would mark just-previewed sales orders **`Oprettet`**, causing them to be skipped on the next real run (the eligibility filter in `UnicontaRepository.ReadAllSalesOrdersAsync` requires `Group` empty or `Fejlet`). If you ran a dry run on a build before this fix, reset the affected orders' JD-status field (`Group`) to empty before going live so they re-transfer.

**1. Enable** in `NeroTrade.JDIntegration/local.settings.json` under `Values` (or as env var `JD__DryRun=true`). Note the config section is `JD`, so the key is `JD__DryRun` (not `JdSettings__…`):

```json
"JD__DryRun": "true"
```

**2. Start the host:**

```bash
dotnet run --project NeroTrade.JDIntegration   # Azure Functions on port 8000
```

**3. Trigger each order type** via the Functions admin endpoint (reads the `xTransferToJD`-flagged orders in company 129192, builds and logs the payload, sends nothing):

```bash
# Purchase orders → JD incoming shipments (clean dry-run, no PDF)
curl -X POST http://localhost:8000/admin/functions/SyncPurchaseOrdersToJd -H "Content-Type: application/json" -d "{}"

# Sales orders → JD request orders
curl -X POST http://localhost:8000/admin/functions/SyncSalesOrdersToJd -H "Content-Type: application/json" -d "{}"
```

The console shows `[DRY-RUN] Skipping POST api/incomingshipments — Payload: {…}` (purchase) and `… api/inventories/{id}/requestorders — Payload: {…}` (sales). Verify the container parent, carrier, text, and that the internal keys (`unit` / `id` / `Sku`) are absent from the line payload.

**Sales-order caveat:** a delivery-note PDF is uploaded to JD *before* the request order. Those file calls are skipped too in DryRun, so you will see `pdf_fail` warnings and `files: []` on the request-order payload — expected and harmless; the request-order payload itself is still logged in full. Purchase orders have no PDF, so their dry-run is completely clean.

> Remember to set `JD__DryRun` back to `false` before deploying or running a real sync.

---

## 11. Multi-project reuse contract

The substrate is project-agnostic. Any future project (new Functions app, n8n flow, other service) joins the Monitoring Subscription by doing **three** things — no changes to Hermes:

1. **Emit log rows** to the same `integration_logs` table with `project = '<your-slug>'` and the schema in §5. Use the same `error_code` taxonomy (extend with `<YOURSUBSYSTEM>_REASON` codes as needed).
2. **Expose `/remediation/*` endpoints** behind the same `X-Remediation-Secret` header pattern. The endpoint URLs can differ — the agent learns them from the `integration_playbooks.remediation.endpoint` field.
3. **Add a Hermes routing rule** mapping `project → Slack channel` (one config row).

That's it. The agent's classifier, playbook lookup, Slack approval flow, and learning loop are all reused unchanged.

---

## 12. File-by-file reference (this repo)

| Path                                                                                   | Purpose                                                                          |
|----------------------------------------------------------------------------------------|----------------------------------------------------------------------------------|
| `Services/Logging/IIntegrationLogger.cs`                                               | `IIntegrationLogger` interface + `IntegrationLogEntry` record (with Phase 1 init-only fields). |
| `Services/Logging/SupabaseIntegrationLogger.cs`                                        | HTTP client posting rows to `integration_logs`. Maps all new columns.            |
| `Services/Logging/NoOpIntegrationLogger.cs`                                            | Fallback when Supabase isn't configured (local dev). No changes needed.          |
| `Services/Logging/IntegrationLogScope.cs`                                              | Scoped DI service holding one `Guid` per invocation. The correlation_id.         |
| `Functions/Sync*.cs` (5 files)                                                         | Inject `IntegrationLogScope`; every `LogAsync` call attaches `CorrelationId`; error/warning calls also set `ErrorCode`, `Retryable`, `SuggestedAction`. |
| `Functions/Admin/RemediationEndpoints.cs`                                              | Three `/remediation/*` endpoints; auth via `RemediationOptions.SharedSecret`; audit logs on every call. |
| `Models/Settings/RemediationOptions.cs`                                                | Shared-secret config binding.                                                    |
| `Program.cs`                                                                           | DI registrations for `IntegrationLogScope` and `RemediationOptions`.             |
| `CLAUDE.md` → "Monitoring & remediation contract"                                      | Short summary + error_code list for in-session AI agents.                        |
| `docs/operations.md` (this file)                                                       | Canonical reference.                                                             |

### Supabase migrations applied

| Name                                              | Adds                                                                                          |
|---------------------------------------------------|-----------------------------------------------------------------------------------------------|
| `integration_logs_agent_actionable_columns`       | New columns on `integration_logs` + indexes + status CHECK constraint.                        |
| `integration_playbooks`                           | Playbook KB table + unique index on `(project, error_code, signature)` + four seed playbooks. |

---

## 13. Roadmap

| Phase     | Scope                                                                                  | Owner          | Status   |
|-----------|----------------------------------------------------------------------------------------|----------------|----------|
| 1         | Rich events + Supabase webhook + App Insights alerts                                   | Repo + manual  | ✅ Code; manual setup pending |
| 2         | `/remediation/*` endpoints + `integration_playbooks` + Hermes Slack approval                 | Repo + Hermes  | ✅ Repo done; Hermes pending  |
| 3         | Hermes classifier + auto-approve graduation                                            | Hermes-side    | Pending  |
| 4 (later) | Agent-generated GitHub PRs for repeating code-level bugs                               | Hermes + GH    | Deferred |

When Phase 3 stabilizes for this project, the same Hermes runtime can onboard the next project via §11 — no new repo work needed.
