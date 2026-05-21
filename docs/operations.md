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
│   (POST /admin/* secret-     │ calls  │                  │ writes │ act OR ask Slack │
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
| **2**     | `/admin/*` remediation endpoints + `integration_playbooks` table seeded. Hermes-side Slack approval buttons.                                   | ✅ Code done — Hermes-side pending |
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

The `/admin/*` endpoints **refuse to run** until this is set, so the surface cannot go live unauthenticated.

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
| `UNICONTA_DUPLICATE_SO`       | Multiple SOs collide on the same number.                                                |
| `UNICONTA_LOOKUP_MISS`        | Expected Uniconta record absent.                                                        |
| `UNICONTA_CRUD_FAILED`        | Uniconta write rejected (usually transient).                                            |
| `UNICONTA_AUTH_FAILED`        | Uniconta session/credentials issue.                                                     |
| `SHIPMONDO_NO_CARRIER`        | No carrier mapping found for an order's postal code.                                    |
| `SHIPMONDO_INVALID_POSTAL`    | Postal code is malformed/unknown.                                                       |
| `PDF_GENERATION_FAILED`       | QuestPDF threw producing a delivery note.                                               |
| `BLOB_UPLOAD_FAILED`          | Pre-signed Azure Blob upload failed.                                                    |
| `MAPPER_NULL_FIELD`           | Mapper hit an unexpected null on a required field.                                      |
| `MAPPER_INVALID_STATE`        | Mapper saw an entity in a shape the code didn't expect.                                 |
| `REMEDIATION_APPLIED`         | Audit row written by `/admin/*` after a successful remediation.                         |
| `REMEDIATION_NOOP`            | `/admin/*` returned a non-success — the underlying call didn't apply.                   |
| `REMEDIATION_FAILED`          | `/admin/*` threw before completing.                                                     |
| `UNKNOWN`                     | Fallback. These go to the Hermes "novel error" reasoning cascade.                       |

---

## 7. `/admin/*` remediation endpoints

All endpoints live in `Functions/Admin/RemediationEndpoints.cs` and require two layers of auth:

1. **Azure Functions key** — `AuthorizationLevel.Function`, sent as `x-functions-key` header or `?code=` query string.
2. **Shared secret** — `X-Remediation-Secret` header, value bound from `Remediation:SharedSecret` config setting.

Every successful call writes an audit row to `integration_logs` with `error_code = REMEDIATION_APPLIED` and the action details in `payload`. Failures write `REMEDIATION_NOOP`. Unhandled throws write `REMEDIATION_FAILED`.

Constant-time comparison is used for the shared secret to avoid leaking length via early exit.

### 7.1 `POST /admin/retry-sales-order/{soNumber}`

Sets the SO's `Group` back to empty + clears `xIntegrationIssue` + flips `xTransferToJD` back to `true`. Next 30-second `SyncSalesOrdersToJd` tick picks it up and re-pushes to JD.

```http
POST https://<host>/api/admin/retry-sales-order/2135
x-functions-key: <function-key>
X-Remediation-Secret: <shared-secret>
```

```json
200 OK
{ "action": "retry-sales-order", "soNumber": 2135, "applied": true }
```

### 7.2 `POST /admin/retry-purchase-order/{poNumber}`

Sets `xJDStatus = "Manuel handling"` + flips `xTransferToJD` back to `true`. Next 40-second `SyncPurchaseOrdersToJd` tick re-pushes.

```http
POST https://<host>/api/admin/retry-purchase-order/4711
x-functions-key: <function-key>
X-Remediation-Secret: <shared-secret>
```

```json
200 OK
{ "action": "retry-purchase-order", "poNumber": 4711, "applied": true }
```

### 7.3 `POST /admin/override-order-status/{orderNumber}`

Forces a sales order's `Group` to a specific value when JD/Uniconta status drifted apart and a tick won't fix it.

```http
POST https://<host>/api/admin/override-order-status/2135
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
    Suggested: POST /admin/retry-sales-order/2135
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
3. Click **Approve** → Hermes calls `/admin/retry-sales-order/2135` with both headers.
4. Confirm:
    - Endpoint returns 200 with `applied = true`.
    - A new `integration_logs` row appears with `error_code = REMEDIATION_APPLIED`, `level = info`, `status = open`.
    - The original error row's `status` flips to `auto_fixed` (Hermes does this — verify via Supabase Dashboard).
    - The next sync tick re-pushes the SO to JD successfully.

### Phase 3 — proactive auto-fix

When live:
- A repeated `UNICONTA_CRUD_FAILED` never reaches Slack — auto-resolves silently via the seed playbook.
- A novel error escalates to Slack on first occurrence, then auto-resolves on subsequent ones (assuming first remediation was approved).

---

## 11. Multi-project reuse contract

The substrate is project-agnostic. Any future project (new Functions app, n8n flow, other service) joins the Monitoring Subscription by doing **three** things — no changes to Hermes:

1. **Emit log rows** to the same `integration_logs` table with `project = '<your-slug>'` and the schema in §5. Use the same `error_code` taxonomy (extend with `<YOURSUBSYSTEM>_REASON` codes as needed).
2. **Expose `/admin/*` endpoints** behind the same `X-Remediation-Secret` header pattern. The endpoint URLs can differ — the agent learns them from the `integration_playbooks.remediation.endpoint` field.
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
| `Functions/Admin/RemediationEndpoints.cs`                                              | Three `/admin/*` endpoints; auth via `RemediationOptions.SharedSecret`; audit logs on every call. |
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
| 2         | `/admin/*` endpoints + `integration_playbooks` + Hermes Slack approval                 | Repo + Hermes  | ✅ Repo done; Hermes pending  |
| 3         | Hermes classifier + auto-approve graduation                                            | Hermes-side    | Pending  |
| 4 (later) | Agent-generated GitHub PRs for repeating code-level bugs                               | Hermes + GH    | Deferred |

When Phase 3 stabilizes for this project, the same Hermes runtime can onboard the next project via §11 — no new repo work needed.
