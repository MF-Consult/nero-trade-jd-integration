# Field Mapping — Uniconta ↔ JD Logistics

> ## ⚠️ AUTHORITATIVE CONTRACT — READ BEFORE TOUCHING ANY MAPPER
>
> This document is the **single source of truth** for how every field is translated between
> Uniconta and JD. It **MUST ALWAYS be respected**.
>
> **Rules for agents and developers:**
> 1. **Never change a mapping** (rename a source field, change a transform, drop/add a JD field)
>    unless the user has **explicitly asked for that specific change** in the current session.
> 2. **Every time a mapping is added or changed in code, it MUST be added/updated here in the same
>    change** — code and this doc never diverge. Add a dated entry to the [Changelog](#changelog).
> 3. Each row is **Fra (Uniconta source) → Til (JD payload field)**, with the exact transform rule.
> 4. If code and this doc disagree, that is a **bug** — stop and reconcile with the user before proceeding.
>
> Code locations: `Services/UnicontaHandler/Mappers/SalesOrderMapper.cs`,
> `Services/UnicontaHandler/Mappers/PurchaseOrderMapper.cs`,
> sourcing in `Services/UnicontaHandler/Repositories/UnicontaRepository.cs`,
> field-name constants in `Services/UnicontaHandler/Constants/UnicontaUserFields.cs`,
> JD DTOs in `Models/ExternalIntegration/RequestOrder.cs` + `IncomingShipment.cs`.

---

## 1. Sales Order → JD Request Order

**Code:** `SalesOrderMapper.Map` · **JD DTO:** `JdRequestOrderCreate` · **Uniconta type:** `DebtorOrderClient` (`o`)

### Eligibility (which orders are picked up)
`UnicontaRepository.ReadAllSalesOrdersAsync` — an order is processed only when **all** hold:
- `xTransferToJD` (`SalesOrderTransferFlag`) = **true**
- `Group` is **empty** OR `"Fejlet"` (i.e. not already `"Oprettet"`)
- `UpdatedAt` within the **last 1 day** (`SalesOrderRecentWindow`, server-side filter)

### Field mapping (Fra → Til)

| JD field (Til) | Uniconta source (Fra) — UI label / field | `LocalSalesOrder` | Transform rule |
|---|---|---|---|
| `date` | **Leveringsdato** (`_DeliveryDate`) + **Tidspunkt for Levering** (`xTimeForDelivery`) | `DeliveryDate` (+ `DeliveryTime`) | Date always from Leveringsdato. **Only** when `DeliveryTime` set **and** product supports `TIMED_DELIVERY`: `finalDate = Leveringsdato.Date + DeliveryTime.TimeOfDay` (date part of xTimeForDelivery is discarded). Else date only. If Leveringsdato is empty, `date` ends up `null`. |
| `text` | **Bemærkning til JD** (`xRemarksForJD`) | `RemarkText` | `"SO {OrderNumber}"` if remark blank; else `"SO {n} - {remark}"` (remark trimmed). The `SO {n}` key **always leads**. |
| `trackingNote` | **Sporingsnote** (`xTrackingNote`) | `TrackingNote` | Raw; blank → `null`. Own dedicated JD field. |
| `deliveryNoteText` | **Note på følgeseddel** (`xTrackingNoteOnLabel`) | `DeliveryNoteText` | Trimmed; blank → `null`. **Label text only — never contains the SO number.** |
| `disableApprovalEmail` | — | — | Always `false`. |
| `address.name` | **Leveringsnavn** (`_DeliveryName`, fallback debtor `_Name`) | `DeliveryName` | Location/company name. |
| `address.att` | **Debitorkonto** (`Account`) | `DebtorAccount` | |
| `address.street` | **Leveringsadresse 1–3** (`_DeliveryAddress1..3`) | `DeliveryAddress1/2/3` | Joined with spaces; blank → `null`. |
| `address.zip` | **Lev. postnr.** (`_DeliveryZipCode`) | `DeliveryZip` | |
| `address.city` | **Lev. by** (`_DeliveryCity`) | `DeliveryCity` | |
| `address.countryCode` / `address.country` | debtor **Land** (`_Country`) | `DeliveryCountryCode` | Normalised via `CountryHelper`; default `"DK"`. |
| `contactPerson.name` | **Leverings-kontaktperson** (`DeliveryContactPerson`) | `DeliveryContactPerson` | Trimmed; blank → `null`. ⚠️ This is the **contact person**, NOT `DeliveryName`. |
| `contactPerson.email` | **Leverings-kontakt-email** (`DeliveryContactEmail`) | `DeliveryContactEmail` | Trimmed; blank → `null`. (No separate `DeliveryEmail` exists on the SDK.) |
| `contactPerson.telephoneDirect` + `telephoneMobile` | **Leveringstelefon** (`DeliveryPhone`) | `DeliveryContactPhone` | Trimmed; blank → `null`. Same value into both fields. |
| `shipmondo.carrierCode` / `productCode` | **Leveringstype** (`xDeliveryType`) / **Transporttype** (`xTransportTypes`) | `DeliveryType` / `TransportType` | See [carrier mapping](#carrier-mapping) below. |
| `shipmondo.productServices` | **Byttepaller** (`xByttepaller`) + leveringstid | `ExchangePallets`, `DeliveryTime` | `PL_EXCHANGE` only if Byttepaller = `"Ja"` **and** product supports it. `TIMED_DELIVERY` only if `DeliveryTime` set **and** product supports it. |
| `shipmondo.carrierInstructions` | **Besked til transportør** (`xMessageForTransport`) | `CarrierMessage` | Raw. |
| `productItems[].quantity` | order line **antal** (`_Qty`) | `Lines[].Quantity` | Rounded to int. |
| `productItems[].catalog.sku` | order line **vare** (`_Item`) | `Lines[].Sku` | Direct. Lines with blank SKU or **service items (`ItemType == 1`)** are skipped (PDF-only, not sent to JD). |
| `files` | generated delivery-note PDF | (PDF pipeline) | Uploaded to JD first, attached with `packageLabel = true`. Skipped/`[]` in DryRun. |

### Carrier mapping
Evaluated only when **not** self-pickup (`xTransportTypes` ≠ `"Afhenter Selv"`):
- `xDeliveryType` = `"GLS"` → `gls` / `GLSDK_BP`
- `xDeliveryType` = `"Palle Fragt"` → `glimoe` / `GLIMOE_PARCEL`
- else if `xTransportTypes` = `"Ekstern Transport"` → `glimoe` / `GLIMOE_PARCEL`
- **DK postal code > 4999** on `GLIMOE_PARCEL` → reroute to `esbjerg_gods_sjaelland` / `EGS_STDPL` (Mikkel, 2026-05-15)
- `"Afhenter Selv"` → **no carrier** (order still sent for picking, no `shipmondo` block)

### Service-code support (`ShipmondoProductCatalog`)
| Product | `TIMED_DELIVERY` | `PL_EXCHANGE` |
|---|---|---|
| `GLSDK_BP` (GLS) | ❌ | ❌ |
| `GLIMOE_PARCEL` (Glimø pallet) | ✅ | ✅ |
| `EGS_STDPL` (EGS pallet) | ❌ | ✅ |

A service sent to a product that does not list it makes JD reject the whole request (`"<code> isn't an allowed service"`).

---

## 2. Purchase Order → JD Incoming Shipment

**Code:** `PurchaseOrderMapper.Map` · **JD DTO:** `JdIncomingShipmentCreate` · **Uniconta type:** `CreditorOrderClient` (`o`)

### Eligibility
`UnicontaRepository.ReadAllPurchaseOrdersAsync` — processed only when **both** hold (no time window; all orders scanned):
- `xTransferToJD` (`PurchaseOrderTransferFlag`) = **true**
- `xJDStatus` is **pending**: empty OR `"Manuel handling"` (`PurchaseOrderJdStatusValues.IsPending`)

### Field mapping (Fra → Til)

| JD field (Til) | Uniconta source (Fra) — UI label / field | `LocalPurchaseOrder` | Transform rule |
|---|---|---|---|
| `date` | **Leveringsdato** (`_DeliveryDate`) | `DeliveryDate` | Fallback `now + 2 days` if blank. |
| `text` | **Bemærkning til JD** (`xRemarksForJD`) | `RemarkText` | `"PO {PurchaseNumber}"` if blank; else `"PO {n} - {remark}"` (trimmed). `PO {n}` key **always leads**. |
| `carrier` | **Speditør** (`xCarrier`) | `Carrier` | `"TBD"` if blank. |
| `notificationEmails` | — | — | Hardcoded `"mb@nerotrade.dk"`. |
| `disableApprovalEmail` | — | — | Always `false`. |
| container **parent** line | **Enhedstype** (`xEnhedstype`) + **Antal enheder** (`xAntalEnheder`) | `ContainerType`, `ContainerCount` | Only when **both** set: emit a pure-container parent line (`isSubItem=false`, `unit=ContainerType`, no SKU) added **first**, so product lines hang under it. |
| `lines[].quantity` | line **antal** (`_Qty`) | `Lines[].Quantity` | Rounded to int. |
| `lines[].isSubItem` | — | — | `true` when a container parent exists, else `false` (flat list). |
| `lines[].externalIdentification` | line **`xExternalSku`** | `Lines[].CustomerItemNumber` | Blank → `null`. |
| `lines[].Sku` *(internal)* | line **vare** (`_Item`) | `Lines[].Sku` | `[JsonIgnore]` — resolved to `catalog.id` against JD's catalog in `JdLogisticsService`; never serialised. A line that cannot resolve must fail loudly, not ship with a bogus id. |
| `lines[].unit` *(internal)* | line **enhed** (`Unit`) / container type | `Lines[].Unit` | `[JsonIgnore]` — matched to JD container types to fill `inventoryContainerType`; never serialised. |

---

## 3. Write-back: JD → Uniconta (status & quantities)

| Uniconta target (Til) | Source (Fra) | When | Code |
|---|---|---|---|
| SO `Group` = `"Oprettet"`, `xIntegrationIssue` cleared, `xJDOrderId` = JD request-order id | JD upsert success | after a sales order is created in JD | `SyncSalesOrdersToJd.HandleBatchAsync` |
| SO `Group` = `"Fejlet"`, `xIntegrationIssue` = reject reason, `xTransferToJD` = `false` | JD upsert failure | JD rejected the order | same |
| SO `Group` ← live JD status | JD request-order status/stage | every status tick | `SyncRequestOrderStatusToUniconta` (+ `StatusMappingConfig`) |
| PO `xJDStatus`; received quantity on PO line | JD registered items | received-quantity tick | `SyncReceivedQuantityToUniconta`, `UpdatePurchaseOrderLineQuantityAsync` |

All write-backs go through `UnicontaService` and are **skipped under DryRun** (see core rule #5).

---

## 4. Core rules — ALWAYS enforced

1. **`SO {n}` / `PO {n}` always leads `text`.** It is a machine key parsed back out by `JdOrderHelper` (dedup, status sync, received-quantity matching). The remark may only ever be appended after `" - "`. Leftmost match wins.
2. **Each Uniconta note lands in its own JD field** — never merge: Bemærkning→`text`, Sporingsnote→`trackingNote`, Note på følgeseddel→`deliveryNoteText`.
3. **Blank text fields are sent as `null`**, not empty string.
4. **Internal resolution keys never reach the payload.** Request-order product lines carry only `quantity` + `catalog.sku` (no `unit`/`id`/internal Sku). Incoming-shipment lines: `unit`/`id`/`Sku` are `[JsonIgnore]` and resolved in-memory before serialisation.
5. **DryRun (`JD__DryRun`) mutates nothing** — neither JD (every mutating call intercepted in `JdRepository.SendWithRetryAsync`) nor Uniconta (every mutating method in `UnicontaService` short-circuits). See [operations.md §10](operations.md).

---

## Changelog

Add a dated entry for **every** mapping change. Newest first.

- **2026-06-17** — Sales-order `contactPerson.name` now sourced from `DeliveryContactPerson` (was `DeliveryName`, which wrongly put the location name in JD's contact field). `contactPerson.email`/`telephone*` normalised to `null` when blank. (`DeliveryContactEmail`→email, `DeliveryPhone`→phone unchanged.)
- **2026-06-17** — Sales-order eligibility window widened 30 min → 1 day (`SalesOrderRecentWindow`); orders flagged the previous day are now picked up.
- **(pre-existing)** — Initial sales-order + purchase-order mappings, carrier mapping incl. EGS reroute for DK zip > 4999, opt-in `PL_EXCHANGE` via `xByttepaller`, `TIMED_DELIVERY` via `xTimeForDelivery`, Lagerhotel container parent/child via `xEnhedstype`/`xAntalEnheder`.
