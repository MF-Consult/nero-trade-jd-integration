# Operations queries (Supabase `integration_logs`)

Read-only SQL for the Supabase project `fmuxfpdjmdypxymhdgme`. Each one answers a question that came up in a
real incident. Queries 1, 2 and 6 were run against production on 2026-11-06; the rest follow the same schema
(see [operations.md §5](operations.md)).

## 1. Is every job still running? (stale-job check)

Alert if a job has not written a `completed` row for longer than ~2.5× its cadence (see operations.md §4.5).

```sql
select regexp_replace(message,' completed in .*','') as job,
       max(created_at) as last_run,
       round(extract(epoch from now()-max(created_at))/60,1) as min_since
from integration_logs
where integration_name='NeroTrade.JDIntegration'
  and message like '% completed in %'
  and created_at > now() - interval '2 days'
group by 1 order by min_since desc;
```

## 2. Errors and warnings by code, last 14 days

```sql
select level, coalesce(error_code,'-') code, count(*) n,
       min(created_at) first_seen, max(created_at) last_seen, count(distinct external_id) ids
from integration_logs
where integration_name='NeroTrade.JDIntegration' and level in ('warning','error')
  and created_at > now() - interval '14 days'
group by 1,2 order by n desc;
```

## 3. Purchase order delay, flag → sent (since 2026-11-06)

`payload.delaySeconds` = Uniconta header `UpdatedAt` → the moment the PO was created in JD.

```sql
select date_trunc('day', created_at) d,
       count(*) pos,
       round(percentile_cont(0.5)  within group (order by (payload->>'delaySeconds')::int)) p50_s,
       round(percentile_cont(0.95) within group (order by (payload->>'delaySeconds')::int)) p95_s,
       max((payload->>'delaySeconds')::int) max_s
from integration_logs
where integration_name='NeroTrade.JDIntegration'
  and message like 'Purchase order % synced to JD.' and payload ? 'delaySeconds'
group by 1 order by 1 desc;
```

## 4. Orders rejected because an item is missing in JD

The most common avoidable rejection: the same root cause as PO 98. Covers sales orders
("We couldn't find any product with the SKU") and purchase orders ("No matching JD catalog item").

```sql
select coalesce(substring(message from 'SKU(?:\(s\))?: ([A-Za-z0-9_.\- ,]+?)(?:\.|"|$)'),'?') missing_sku,
       count(*) rejections, count(distinct external_id) orders,
       min(created_at)::date first_d, max(created_at)::date last_d,
       array_agg(distinct external_id) ids
from integration_logs
where integration_name='NeroTrade.JDIntegration' and error_code='JD_VALIDATION_REJECTED'
  and (message like '%find any product with the SKU%' or message like '%No matching JD catalog item%')
  and created_at > now() - interval '30 days'
group by 1 order by rejections desc;
```

## 5. JD webhook deliveries (after the webhook is registered)

One info row per accepted event. Compare with the status updates polling finds.

```sql
select date_trunc('day', created_at) d, payload->>'webhookTypeName' type, count(*) events
from integration_logs
where integration_name='NeroTrade.JDIntegration' and message like 'JD webhook % received%'
group by 1,2 order by 1 desc, 3 desc;
```

## 6. Slow runs (jobs that block the dispatcher tick)

```sql
select regexp_replace(message,' completed in .*','') job, count(*) runs,
       round(avg(duration_ms)) avg_ms, round(percentile_cont(0.99) within group (order by duration_ms)::numeric) p99_ms,
       max(duration_ms) max_ms, count(*) filter (where duration_ms>10000) over_10s
from integration_logs
where integration_name='NeroTrade.JDIntegration' and message like '% completed in %'
  and created_at > now() - interval '7 days'
group by 1 order by avg_ms desc;
```

Baseline 2026-11-06: `SyncRequestOrderStatusToUniconta` avg 6.7 s, max 149 s (it reads *all* ~1.470 JD request
orders each run, 8 pages of 200); every other job is under 2.1 s average.

## 7. Auto-created catalog items (only with `PurchaseOrderSync__AutoCreateMissingItems=true`)

```sql
select created_at, external_id as sku, message
from integration_logs
where integration_name='NeroTrade.JDIntegration' and message like '%auto-created in JD%'
order by created_at desc limit 50;
```
