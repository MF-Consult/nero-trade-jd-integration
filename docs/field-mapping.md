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
| `text` | **Bemærkning til JD** (`xRemarksForJD`) | `RemarkText` | The remark **only** (trimmed); blank → `null`. The `SO {n}` key is **no longer written here** — it moved to `trackingNote` (2026-07-15). |
| `trackingNote` | **Sporingsnote** (`xTrackingNote`) + **Ordrenr.** (`OrderNumber`) | `TrackingNote` (+ `OrderNumber`) | `"{Sporingsnote} / SO {n}"`, or bare `"SO {n}"` when Sporingsnote blank. **JD caps this field at 30 chars** (it is the shipping label) — the Sporingsnote is trimmed as needed so the appended `SO {n}` key is **never truncated**. This is the machine key `JdOrderHelper` parses back out (end-anchored) for dedup/status sync. |
| `deliveryNoteText` | **Note på følgeseddel** (`xTrackingNoteOnLabel`) | `DeliveryNoteText` | Trimmed; blank → `null`. **Label text only — never contains the SO number.** |
| `disableApprovalEmail` | — | — | Always `false`. |
| `address.name` | **Leveringsnavn** (`_DeliveryName`, fallback debtor `_Name`) | `DeliveryName` | Location/company name. |
| `address.att` | **Debitorkonto** (`Account`) | `DebtorAccount` | |
| `address.street` | **Leveringsadresse 1–3** (`_DeliveryAddress1..3`) | `DeliveryAddress1/2/3` | Joined with spaces; blank → `null`. |
| `address.zip` | **Lev. postnr.** (`_DeliveryZipCode`) | `DeliveryZip` | |
| `address.city` | **Lev. by** (`_DeliveryCity`) | `DeliveryCity` | |
| `address.countryCode` / `address.country` | debtor **Land** (`_Country`) | `DeliveryCountryCode` | `CountryCode` enum → ISO alpha-2 via `UnicontaCountry.ToIsoCode` (repository), then `CountryHelper` (adds the English name for the ~30 countries in its table; otherwise `country` = the ISO code). Land empty (`Unknown`) → `"DK"`. `countryCode` is **never** a country name. |
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
| container **parent** line | **Enhedstype** (`xEnhedstype`) + **Antal enheder** (`xAntalEnheder`) | `ContainerType`, `ContainerCount` | Only when **both** set: emit a pure-container parent line (`isSubItem=false`, `unit=ContainerType` through `UnitTranslator`, no SKU) added **first**, so product lines hang under it. `xEnhedstype` is free text already typed in Danish ("Palle"), so the translation is normally a no-op. |
| `lines[].quantity` | line **antal** (`_Qty`) | `Lines[].Quantity` | Rounded to int. |
| `lines[].isSubItem` | — | — | `true` when a container parent exists, else `false` (flat list). |
| `lines[].externalIdentification` | line **`xExternalSku`** | `Lines[].CustomerItemNumber` | Blank → `null`. |
| `lines[].Sku` *(internal)* | line **vare** (`_Item`) | `Lines[].Sku` | `[JsonIgnore]` — resolved to `catalog.id` against JD's catalog in `JdLogisticsService`; never serialised. A line that cannot resolve must fail loudly, not ship with a bogus id. **Cost items are dropped first** (open order *and* posted invoice): a line whose item number, stored line text (`_Text`) or item name matches `NonWarehouseItems` — `PRINTPLA`, `Forud`, `1200` (Cargo - Internal), `7` (Cargo Transport) — is never sent. |
| `lines[].unit` *(internal)* | line **enhed** (`Unit`) / container type | `Lines[].Unit` | `[JsonIgnore]` — **translated to JD's container-type naming by `UnitTranslator`**, then matched to JD container types to fill `inventoryContainerType`; never serialised. |

#### Unit → JD container type (`UnitTranslator`)

Uniconta returns a line's unit as the **English** `ItemUnit` enum name; JD names its container types with the **Danish** label the Uniconta UI shows the user. Both directions of a name match must therefore be translated — an untranslated unit falls back to Stk.

| Uniconta `Unit` (enum name) | Uniconta UI (dansk) | JD container type | JD id |
|---|---|---|---|
| `Pcs` | Stk | `Stk` | 15 |
| `Packages` | Kolli | `Kolli` | 13 |
| `Pallet` | Palle | `Palle` | 3 |
| `Container` | Container | `Container` | 1 |

Anything else passes through unchanged (so free-text `xEnhedstype` keeps working). A unit that still matches no JD container type is sent as **Stk** and logged as `JD_CONTAINER_TYPE_UNMAPPED` (warning, not retryable) — never silently.

### 2a. Safety-net: posted purchase invoice → JD Incoming Shipment

**Code:** `SyncPostedPurchaseInvoicesToJd` + `PurchaseOrderMapper.Map(LocalPurchaseInvoice)` · **Uniconta type:** `CreditorInvoiceClient` (`inv`)

Catches purchase orders **booked (bogført) before** `xTransferToJD` was set — once booked, the order leaves the open-order table section 2 reads, so it would never register at JD. Eligibility mirrors section 2 (`xTransferToJD` = true **and** `xJDStatus` pending), scanned over a recent-posting-date window on the booked invoice.

**The mapping is identical to section 2 — by construction.** Both `Map(LocalPurchaseOrder)` and `Map(LocalPurchaseInvoice)` funnel through the shared private `PurchaseOrderMapper.BuildIncomingShipment(...)`, so `carrier`, the container/kolli parent, `text` (`"PO {n}[ - remark]"`), `notificationEmails`, `disableApprovalEmail` and the line structure are the same. The header fields (`xCarrier`, `xRemarksForJD`, `xEnhedstype`, `xAntalEnheder`) are read off the **booked invoice header** — Uniconta copies the originating PO's user fields onto it (the same way `xTransferToJD`/`xJDStatus` are already read there). If a field is unpopulated it degrades to the section-2 fallback (`"TBD"` / no parent). Dedup identity `"PO {originatingOrderNumber}"` is shared with section 2, so an order already sent via either path is skipped, never duplicated.

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

1. **The `SO {n}` / `PO {n}` machine key is parsed back out by `JdOrderHelper`** (dedup, status sync, received-quantity matching).
   - **Sales orders:** `SO {n}` is **appended to `trackingNote`** as `"{Sporingsnote} / SO {n}"` and read **end-anchored** (only at string-start or after our exact `" / "` separator), so a stray `SO …` inside a free Sporingsnote is ignored. Read priority is `shopOrderId → trackingNote → text → deliveryNoteText`; the `text`/`deliveryNoteText` fallbacks match **legacy** orders whose key led `text` (or the delivery-note text), so in-flight orders are not re-sent as "new".
   - **Purchase orders:** `PO {n}` still **leads `text`** (`"PO {n} - {remark}"`); the remark may only ever be appended after `" - "`, leftmost match wins.
2. **Each Uniconta note lands in its own JD field**: Bemærkning→`text`, Sporingsnote→`trackingNote`, Note på følgeseddel→`deliveryNoteText`. The **only** deliberate merge is the sales-order `SO {n}` machine key, appended to `trackingNote` after the Sporingsnote (rule 1) — it needs a home now that it no longer sits on `text`.
3. **Blank text fields are sent as `null`**, not empty string.
4. **Internal resolution keys never reach the payload.** Request-order product lines carry only `quantity` + `catalog.sku` (no `unit`/`id`/internal Sku). Incoming-shipment lines: `unit`/`id`/`Sku` are `[JsonIgnore]` and resolved in-memory before serialisation.
5. **DryRun (`JD__DryRun`) mutates nothing** — neither JD (every mutating call intercepted in `JdRepository.SendWithRetryAsync`) nor Uniconta (every mutating method in `UnicontaService` short-circuits). See [operations.md §10](operations.md).

---

## Changelog

Add a dated entry for **every** mapping change. Newest first.

- **2026-09-29** — Purchase lines: **cost items are excluded** from the JD incoming shipment via a static list (`Constants/NonWarehouseItems.cs`): `PRINTPLA` (printplader), `Forud` (forudbetalinger), `1200` "Cargo - Internal" and `7` "Cargo Transport" (transport; all four are type *Ydelse* in Uniconta) — agreed with Maiwand (Nero Trade) the same day. Trigger: PO 26 was rejected in full (`JD_VALIDATION_REJECTED`, "No matching JD catalog item for SKU(s): PRINTPLA, Forud"), because the **open-order** path sent every line with an item number, while the posted-invoice path already kept only group `Stock`. Applied in the repository projections of both paths (so `/inspect` shows it too); exact, case-insensitive match on item number, stored line text (`_Text`) or item name. Name matching is best-effort — the SDK's `Text`/item-name getters need the client item cache (without it they return null or throw, so the raw `_Text` field is read and the name getter is guarded) — so add the **item number** of any new cost item. A real stock item missing from JD still fails the shipment loudly, unchanged.
- **2026-09-28** — **Country → ISO alpha-2** for catalog items (`producedInCountryCode` ← varens **Oprindelsesland**, `CountryOfOrigin`), debtors (`countryCode`) and sales-order delivery addresses (`address.countryCode`). The repository used `CountryCode.ToString()`, which is the enum **name** ("Türkiye", "UnitedKingdom"), and `CountryHelper`'s fallback passed any name outside its ~30-country table through as the code — item TRBFTELIPWW610 (Türkiye) reached JD as `"TÜRKIYE"` and was rejected (`JD_VALIDATION_REJECTED`: must be 2 characters). Multi-word countries (`UnitedKingdom`, `UnitedStates`) never matched the table either. Fix: new `UnicontaCountry.ToIsoCode` maps via the SDK's `CountryISOCode` enum (same ordinals; all 248 countries verified, Guadeloupe special-cased to `GP`); `CountryHelper` now passes an unknown two-letter code through and never returns a name as the code. Item/debtor defaults unchanged (`"DK"` when Land/Oprindelsesland is empty).
- **2026-07-27** — Purchase-order line **unit** is now translated to JD's container-type naming (`UnitTranslator`, new table under §2) instead of being compared raw. Uniconta hands us the **English** `ItemUnit` enum name (`"Packages"`, `"Pcs"`, `"Pallet"`), JD names its container types in **Danish** (`Kolli`, `Stk`, `Palle` — the same labels the Uniconta UI shows), so `SetContainerTypesAsync`'s exact-name match never hit and **every** product line fell back to the `Stk` default. Confirmed in production: PO 33/37/39 all carry `Unit = "Packages"` (= Kolli) in Uniconta and were registered in JD as `Stk`; 33 of the then-open purchase-order lines were `Packages`. Translation happens in `PurchaseOrderMapper` (so `/inspect/purchase-order/{n}` shows the value that will actually be matched) and is applied to both the product lines and the `xEnhedstype` container parent — unknown units pass through unchanged, so the free-text Danish parent is unaffected. Second half of the fix: an unresolvable unit still degrades to `Stk` (JD requires a container type) but now emits a `JD_CONTAINER_TYPE_UNMAPPED` warning to `integration_logs` — the old silent fallback is what let this run unnoticed. Reverse direction (`SyncReceivedQuantityToUniconta`) unchanged; JD's catalog API has **no** unit field, so a unit can only ever ride on a shipment line, never on the item card.
- **2026-07-22** — Posted-invoice **safety-net** (`SyncPostedPurchaseInvoicesToJd`) reaches **full parity** with the open-order path (new §2a). Previously it hardcoded `carrier = "TBD"`, had **no container logic** (everything shipped as flat "stk", never a kolli/pallet parent), suppressed notifications (`notificationEmails = null`, `disableApprovalEmail = true`) and forced bare `text = "PO {n}"` (dropping the remark). Root cause was a separate, reduced-fidelity `Map(LocalPurchaseInvoice)`. Fix: both mapper overloads now delegate to one shared `PurchaseOrderMapper.BuildIncomingShipment(...)`, so carrier (`xCarrier`), container parent (`xEnhedstype`/`xAntalEnheder`), remark (`xRemarksForJD`), notification/approval fields and line structure are identical **by construction**. `LocalPurchaseInvoice` now carries those header fields; `ReadPostedPurchaseInvoicesAsync` reads them off `CreditorInvoiceClient` (same user fields as the open-order read). `LocalPurchaseInvoiceLine.IsSubItem` removed (now derived from whether a container parent exists, like the PO path). **Behavioural note:** safety-net shipments now trigger the same JD approval/notification emails as normal ones (deliberate — "match the normal path always").
- **2026-07-15** — Sales-order `SO {n}` machine key **moved off `text` onto `trackingNote`**, appended after the Sporingsnote as `"{Sporingsnote} / SO {n}"` (bare `"SO {n}"` when the Sporingsnote is blank). `text` now carries the `xRemarksForJD` remark **only** (blank → `null`). The Sporingsnote is trimmed as needed to keep the key whole within JD's **30-char `trackingNote` cap** (verified against the JD swagger: "Max: 30 Chars … Shipping Label") — a silent truncation of the key would break dedup. `JdOrderHelper.GetOrderNumberString`/`GetOrderNumber` gained a `trackingNote` parameter and parse it **first** (end-anchored), falling back to `text` then `deliveryNoteText` so already-sent (in-flight) orders keyed on `text` still match and are **not** re-sent. Dedup (`JdLogisticsService.UpsertRequestOrdersAsync`), status sync (`SyncRequestOrderStatusToUniconta`), and the delete/get sales-order admin endpoints all updated to pass `trackingNote`. Purchase-order path (`PO {n}` on `text`) unchanged. Source field for the number is the Uniconta SDK's `DebtorOrderClient.OrderNumber`.
- **2026-06-17** — Sales-order `contactPerson.name` now sourced from `DeliveryContactPerson` (was `DeliveryName`, which wrongly put the location name in JD's contact field). `contactPerson.email`/`telephone*` normalised to `null` when blank. (`DeliveryContactEmail`→email, `DeliveryPhone`→phone unchanged.)
- **2026-06-17** — Sales-order eligibility window widened 30 min → 1 day (`SalesOrderRecentWindow`); orders flagged the previous day are now picked up.
- **(pre-existing)** — Initial sales-order + purchase-order mappings, carrier mapping incl. EGS reroute for DK zip > 4999, opt-in `PL_EXCHANGE` via `xByttepaller`, `TIMED_DELIVERY` via `xTimeForDelivery`, Lagerhotel container parent/child via `xEnhedstype`/`xAntalEnheder`.
