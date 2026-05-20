# Supabase Integration Logging — Portable Spec

A reusable contract for emitting agent-readable integration events to a shared Supabase `integration_logs` table. Designed for the **Monitoring Subscription** substrate: one Hermes agent runtime serves many projects, all writing to the same table with a `project` discriminator.

This document is **project-agnostic** — copy it (or a link to it) into any new repo that joins the substrate. C# excerpts come from the Nero Trade JD Integration reference implementation, but the contract maps cleanly onto TypeScript, Python, n8n, or anything else that can `POST` JSON to PostgREST.

> Companion docs: this repo's `docs/operations.md` covers the full Hermes loop, `/admin/*` endpoints, and playbook learning. This file isolates the **logging layer** so it can travel.

---

## 1. The mental model

Every integration produces three plumbing pieces:

1. **Events** — structured rows written to `public.integration_logs`. One row per business outcome (sync completed, JD rejected order, blob upload failed).
2. **Correlation** — every row from one invocation shares a `correlation_id` (UUID v4), so the agent can pull the full story with a single query.
3. **Actionability** — `error`/`warning` rows carry `error_code` (taxonomy), `retryable` (bool), and `suggested_action` (free text). These are the only three fields the Hermes classifier needs to decide auto-retry vs. ask-in-Slack.

If you write rows that satisfy those three, the Hermes substrate works for your project out of the box.

---

## 2. The shared table — `public.integration_logs`

Schema (already applied to the shared Supabase project; do **not** re-create it locally):

| Column            | Type         | Required          | Notes                                                                 |
|-------------------|--------------|-------------------|-----------------------------------------------------------------------|
| `id`              | bigint       | server            | PK, identity                                                          |
| `created_at`      | timestamptz  | server (default `now()`) |                                                                |
| `integration_name`| text         | **caller**        | Slug for the writing system, e.g. `nero-trade-jd-integration`         |
| `level`           | text         | **caller**        | `info` / `warning` / `error`                                          |
| `source_system`   | text         | **caller**        | The subsystem the event belongs to (`Uniconta`, `JD`, `Integration`, …) |
| `external_id`     | text         | when relevant     | Business id — order number, sku, debtor account                       |
| `message`         | text         | **caller**        | Human-readable, **single-line, sanitized** (see §5)                   |
| `stack_trace`     | text         | **leave null**    | App Insights / Sentry / your APM owns this. Never write user-facing stack traces here. |
| `payload`         | jsonb        | optional          | Rich context blob — request, response, snapshot, summary metrics      |
| `error_code`      | text         | error/warning     | Stable taxonomy key, SCREAMING_SNAKE (see §4)                         |
| `correlation_id`  | uuid         | **caller**        | One per invocation; same value on every row from that invocation      |
| `retryable`       | boolean      | error/warning     | Hint to the agent — auto-retry vs. escalate                           |
| `attempt`         | integer      | optional          | 1-based; reserved for the retry orchestrator                          |
| `suggested_action`| text         | error/warning     | Free-text hint the agent shows in Slack                               |
| `status`          | text         | server (default `open`) | `open` / `ack` / `auto_fixed` / `resolved` / `wontfix` (CHECK)  |
| `resolution`      | jsonb        | Hermes-side       | What the agent did                                                    |
| `slack_thread_ts` | text         | Hermes-side       | Set when the agent opens a Slack thread for this incident             |
| `project`         | text         | **caller**        | Routing key — `nero-trade-jd-integration`, etc.                       |

Indexes: `(status, level)`, `correlation_id`, `project`, partial on `error_code`.

> **RLS note:** Currently disabled because callers use the service-role key. When onboarding projects that use anon/restricted keys, enable RLS and add a service-role allow policy.

---

## 3. The five contract rules

Follow these literally — the agent doesn't infer.

### Rule 1 — One `correlation_id` per invocation

A function invocation = one UUID. Hold it as a local variable, **not** a DI-scoped singleton. In Azure Functions isolated worker (and most worker hosts) "scoped" lifetimes collapse to singleton across concurrent runs and correlation ids leak.

```csharp
// Reference implementation
public sealed class IntegrationLogScope
{
    public Guid CorrelationId { get; } = Guid.NewGuid();
}

// At the top of every RunAsync:
var logScope = new IntegrationLogScope();
```

Pass `logScope` (or just the `Guid`) into helper methods explicitly.

### Rule 2 — Set `error_code`, `retryable`, `suggested_action` on every `error`/`warning`

These three fields are the agent's classifier inputs. Don't leave them null. Use the taxonomy in §4.

```csharp
await integrationLogger.LogAsync(new IntegrationLogEntry(
    integrationName, "error", "JD", orderNumber.ToString(),
    $"JD rejected sales order {orderNumber}: {LogSanitizer.Sanitize(reason)}",
    StackTrace: null,
    Payload: JsonSerializer.SerializeToElement(new { errorMessage = reason, orderNumber }))
{
    CorrelationId = logScope.CorrelationId,
    ErrorCode = "JD_VALIDATION_REJECTED",
    Retryable = false,
    SuggestedAction = "Manual review — order was marked Failed in Uniconta with the JD reject reason."
}, ct);
```

### Rule 3 — Never write `stack_trace`

Stack traces leak: connection strings, JD response bodies with customer/order data, auth headers. Supabase is a substrate, not a trusted PII vault.

- `Message` = `ExceptionTypeName + ": " + Sanitize(ex.Message)`.
- `StackTrace` = `null`.
- Full stack still goes to App Insights / Sentry via the local `ILogger`.

### Rule 4 — Sanitize anything that could carry CR/LF

External API responses (JD, Shipmondo) and `Exception.Message` can contain newlines, which corrupt log rows and let a future query loop see one event as several. Strip CR/LF on every value that flows from external input into `Message`.

### Rule 5 — Top-level catch classifies transport vs. code

Don't dump every catch under `SYNC_RUN_FAILED`. The agent can't distinguish a transient 503 from a deploy-bug if you do.

```csharp
catch (Exception ex)
{
    logger.LogError(ex, "MySync failed");
    var classified = ErrorCodeClassifier.Classify(ex);
    await integrationLogger.LogAsync(new IntegrationLogEntry(
        integrationName, "error", "Integration", null,
        $"MySync run failed: {LogSanitizer.Describe(ex)}", null, null)
    {
        CorrelationId = logScope.CorrelationId,
        ErrorCode = classified.ErrorCode,
        Retryable = classified.Retryable,
        SuggestedAction = classified.SuggestedAction
    }, CancellationToken.None);
    throw;
}
```

---

## 4. `error_code` taxonomy

Convention: `SUBSYSTEM_REASON`, SCREAMING_SNAKE. Free to extend — no migration needed.

Universal (use as-is in every project):

| Code                          | Meaning                                                                    | Typical `retryable` |
|-------------------------------|----------------------------------------------------------------------------|---------------------|
| `SYNC_RUN_FAILED`             | Top-level catch fallback when nothing more specific fits                   | true                |
| `JD_TIMEOUT`                  | Network-level timeout on the upstream call                                 | true                |
| `JD_AUTH_FAILED`              | Upstream rejected the bearer token (401/403)                               | **false**           |
| `JD_RATE_LIMITED`             | Upstream returned 429                                                      | true                |
| `JD_5XX`                      | Upstream server error                                                      | true                |
| `JD_VALIDATION_REJECTED`      | Upstream accepted the request but rejected the payload                     | **false**           |
| `JD_LOOKUP_MISS`              | Upstream returned 404 for an expected record                               | true                |
| `MAPPER_NULL_FIELD`           | Mapper hit an unexpected null on a required field                          | **false**           |
| `MAPPER_INVALID_STATE`        | Mapper saw an entity in a shape the code didn't expect                     | **false**           |
| `UNKNOWN`                     | Last-resort fallback. Hermes opens the "novel error" reasoning cascade.    | true                |

> `JD_` is conventional for "the upstream service this integration owns" — rename for your project (`SHOPIFY_`, `STRIPE_`, `SAP_`, …). Keep the suffixes (`_TIMEOUT`, `_AUTH_FAILED`, `_RATE_LIMITED`, `_5XX`, `_VALIDATION_REJECTED`, `_LOOKUP_MISS`) — Hermes pattern-matches them.

Project-specific examples from the Nero Trade integration: `UNICONTA_CRUD_FAILED`, `UNICONTA_DUPLICATE_SO`, `SHIPMONDO_NO_CARRIER`, `PDF_GENERATION_FAILED`, `BLOB_UPLOAD_FAILED`.

---

## 5. Reference helpers

Two tiny helpers cover Rules 3, 4, and 5. Port them verbatim in any language.

### 5.1 `LogSanitizer`

```csharp
public static class LogSanitizer
{
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace('\r', ' ').Replace('\n', ' ');
    }

    public static string Describe(Exception ex) =>
        ex.GetType().Name + ": " + Sanitize(ex.Message);
}
```

### 5.2 `ErrorCodeClassifier`

Maps a thrown `Exception` to a `(error_code, retryable, suggested_action)` triple. Inspect HTTP status + exception type — that's enough for 90% of catches.

```csharp
public sealed record ClassifiedError(string ErrorCode, bool Retryable, string SuggestedAction);

public static class ErrorCodeClassifier
{
    public static ClassifiedError Classify(Exception ex)
    {
        if (ex is TaskCanceledException or OperationCanceledException
            || ex.InnerException is TimeoutException)
            return new("JD_TIMEOUT", true,
                "Transient — next scheduled tick will retry. Investigate if it persists.");

        if (ex is HttpRequestException http)
        {
            var status = http.StatusCode;
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new("JD_AUTH_FAILED", false,
                    "Rotate the bearer token in app settings — auth was rejected.");
            if (status == HttpStatusCode.TooManyRequests)
                return new("JD_RATE_LIMITED", true,
                    "Upstream throttled; next tick will retry after the rate window resets.");
            if (status.HasValue && (int)status >= 500)
                return new("JD_5XX", true, "Upstream-side failure; next tick will retry.");
        }

        return new("SYNC_RUN_FAILED", true,
            "Inspect stack trace in App Insights; if transient (timeout/network), the next tick will retry.");
    }
}
```

Rename `JD_*` codes to fit your domain.

---

## 6. Reference logger — Supabase via PostgREST

Service-role POST to `${SUPABASE_URL}/rest/v1/integration_logs` with `Prefer: return=minimal`. Failures fall back to local logger — **logging must never break the main flow**.

```csharp
public sealed class SupabaseIntegrationLogger : IIntegrationLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly ILogger<SupabaseIntegrationLogger> _logger;

    public async Task LogAsync(IntegrationLogEntry entry, CancellationToken cancellationToken)
    {
        var payload = new
        {
            integration_name  = entry.IntegrationName,
            level             = entry.Level,
            source_system     = entry.SourceSystem,
            external_id       = entry.ExternalId,
            message           = entry.Message,
            stack_trace       = entry.StackTrace,
            payload           = entry.Payload,
            error_code        = entry.ErrorCode,
            correlation_id    = entry.CorrelationId,
            retryable         = entry.Retryable,
            attempt           = entry.Attempt,
            suggested_action  = entry.SuggestedAction
            // status defaults to 'open' server-side
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "integration_logs")
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            request.Headers.Add("Prefer", "return=minimal");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write log entry to Supabase. {Level} {Source} {Message}",
                entry.Level, entry.SourceSystem, entry.Message);
        }
    }
}
```

DI registration:

```csharp
builder.Services.AddHttpClient<IIntegrationLogger, SupabaseIntegrationLogger>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<SupabaseOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/rest/v1/");
    client.DefaultRequestHeaders.Add("apikey", opts.ServiceRoleKey);
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opts.ServiceRoleKey);
    client.Timeout = TimeSpan.FromSeconds(30);
});
```

Required config (env or appsettings):

| Setting                 | Example                                              |
|-------------------------|------------------------------------------------------|
| `Supabase__BaseUrl`     | `https://fmuxfpdjmdypxymhdgme.supabase.co`           |
| `Supabase__ServiceRoleKey` | service-role JWT (never commit, never share)      |
| `Supabase__IntegrationName` | `your-project-slug`                              |

When the service-role key is absent (local dev), fall back to a `NoOpIntegrationLogger` so the main sync flow stays untouched.

---

## 7. Onboarding a new project in 4 steps

1. **Pick a `project` slug.** Lowercase, kebab-case, stable. E.g. `helmstad-shopify-sync`. Use it as both the `project` column value and as the `IntegrationName` if they're the same.
2. **Add `LogSanitizer` + `ErrorCodeClassifier` + `SupabaseIntegrationLogger`** (or the equivalent in your language). Three files, ~120 LOC total.
3. **Wire the contract:**
   - One `IntegrationLogScope` (or local UUID) per invocation.
   - Every `LogAsync` carries `CorrelationId`.
   - Every `error`/`warning` carries `ErrorCode`, `Retryable`, `SuggestedAction`.
   - `Message` is sanitized, `StackTrace` is null.
   - Top-level catch uses `ErrorCodeClassifier.Classify(ex)`.
4. **Tell Hermes:** add one config row mapping `project → Slack channel`. The classifier, playbook lookup, and learning loop reuse unchanged.

That's it. The substrate is project-agnostic by design — see `docs/operations.md` §11 for the full reuse contract.

---

## 8. Anti-patterns to avoid

These mistakes were caught in the Nero Trade code review; documented here so they don't repeat.

- **DI-scoped `IntegrationLogScope` in an isolated worker.** Scoped lifetime collapses to singleton; two concurrent timer runs share a `correlation_id`. Instantiate locally per invocation.
- **`ex.ToString()` in `Message` or `StackTrace`.** Leaks PII, connection strings, response bodies. Use `LogSanitizer.Describe(ex)` instead.
- **`Dictionary<string?, …>`.** CS8714 warning, type lies about null-safety. Guard nulls at the call site instead.
- **Static `HttpClient` without `Timeout`.** A hanging upload holds the run-lock semaphore permanently. Always set an explicit `Timeout`.
- **Top-level catch using one generic error code.** Classify transport (`_TIMEOUT`, `_5XX`, `_AUTH_FAILED`, `_RATE_LIMITED`) vs. code (`SYNC_RUN_FAILED`) so Hermes can route correctly.
- **Silent failure paths in non-critical pipelines.** PDF generation failing during a sync still emits a `warning` row with `PDF_GENERATION_FAILED` — don't let things go invisible just because the main flow continues.
- **Interpolating `ex.Message` directly into `Message`.** Newlines in the message can be mistaken for separate log events by a future query loop. Sanitize first.
- **Unbounded debug endpoints that proxy arbitrary paths to upstream APIs.** Function-key gating isn't enough — anyone with the key can hit your full upstream surface with the service's credentials. Whitelist paths + regex-validate identifiers.

---

## 9. Minimal payload checklist before merging a new project

- [ ] Every `LogAsync` call sets `CorrelationId`.
- [ ] Every `error`/`warning` sets `ErrorCode`, `Retryable`, `SuggestedAction`.
- [ ] `Message` is single-line and sanitized.
- [ ] `StackTrace` is `null` everywhere.
- [ ] Top-level catches use a classifier (or hand-roll the same mapping).
- [ ] `project` column is set on every write.
- [ ] Service-role key is in config/secrets, not in source.
- [ ] Logger fails closed (logs locally) when Supabase write errors — never throws into the main flow.
- [ ] A `NoOpIntegrationLogger` (or equivalent) covers local dev when Supabase isn't configured.
