using Uniconta.DataModel;

namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Repositories;

using Models;
using Constants;
using Uniconta.ClientTools.DataModel;
using Uniconta.Common;
using Microsoft.Extensions.Logging;

public class UnicontaRepository(UnicontaConnectionManager connectionManager, ILogger<UnicontaRepository> logger) : IUnicontaRepository
{
    private readonly ILogger<UnicontaRepository> _logger = logger;
    public async IAsyncEnumerable<LocalDebtor> ReadAllDebtorsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var queryApi = await connectionManager.CreateQueryApiAsync();
        // No filters -> fetch all
        var debtors = await queryApi.Query<DebtorClient>((IEnumerable<PropValuePair>?)null);
        foreach (var d in (debtors ?? Enumerable.Empty<DebtorClient>()).Where(d => d != null && d.GetUserFieldBoolean(UnicontaUserFields.DebtorTransferFlag)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new LocalDebtor
            {
                DebtorAccount = d.Account,
                Name = d.Name,
                Address1 = d.Address1,
                Address2 = d.Address2,
                ZipCode = d.ZipCode,
                City = d.City,
                Country = d.Country.ToString(),
                CountryCode = d._Country == 0 ? null : d._Country.ToString()
            };
        }
    }

    public async IAsyncEnumerable<LocalInventoryItem> ReadAllItemsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var queryApi = await connectionManager.CreateQueryApiAsync();
        var items = await queryApi.Query<InvItemClient>((IEnumerable<PropValuePair>?)null);
        foreach (var i in (items ?? Enumerable.Empty<InvItemClient>()).Where(i => i != null && i.GetUserFieldBoolean(UnicontaUserFields.ItemTransferFlag)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new LocalInventoryItem
            {
                Sku = i.Item,
                Name = i.Name,
                Description = i.ItemNames?.FirstOrDefault(n => n.ItemNameGroup == "da")?.Text ?? string.Empty,
                EnDescription = i.ItemNames?.FirstOrDefault(n => n.ItemNameGroup == "en")?.Text ?? string.Empty,
                CommodityCode = i.TariffNumber,
                UnitPrice = (decimal?)i.SalesPrice1,
                UnitWeightInGrams = (int?)(i.NetWeight * 1000),
                ProducedInCountryCode = i.CountryOfOrigin?.ToString(),
                Barcodes = []
            };
        }
    }

    public async IAsyncEnumerable<LocalPurchaseOrder> ReadAllPurchaseOrdersAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var queryApi = await connectionManager.CreateQueryApiAsync();
        var orders = await queryApi.Query<CreditorOrderClient>((IEnumerable<PropValuePair>?)null);
        foreach (var o in (orders ?? Enumerable.Empty<CreditorOrderClient>()).Where(o => o != null && o.GetUserFieldBoolean(UnicontaUserFields.PurchaseOrderTransferFlag) && PurchaseOrderJdStatusValues.IsPending(o.GetUserField(UnicontaUserFields.PurchaseOrderJdStatus) as string)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var po = new LocalPurchaseOrder
            {
                PurchaseNumber = o.OrderNumber,
                Date = o._Created,
                SupplierAccount = o.Account,
                OurRef = o._OurRef,
                YourRef = o._YourRef,
                Requisition = o._Requisition,
                DeliveryName = o._DeliveryName,
                DeliveryAddress1 = o._DeliveryAddress1,
                DeliveryAddress2 = o._DeliveryAddress2,
                DeliveryAddress3 = o._DeliveryAddress3,
                DeliveryZip = o._DeliveryZipCode,
                DeliveryCity = o._DeliveryCity,
                DeliveryCountryCode = o._DeliveryCountry == 0 ? "DK" : o._DeliveryCountry.ToString(),
            };

            // Fetch detail lines using Master/Detail query to ensure lines are loaded
            var masters = new List<UnicontaBaseEntity> { o };
            var lines = await queryApi.Query<CreditorOrderLineClient>(masters, null);
            foreach (var l in lines ?? Enumerable.Empty<CreditorOrderLineClient>())
            {
                po.Lines.Add(new LocalPurchaseOrderLine
                {
                    Sku = l._Item,
                    Quantity = l._Qty,
                    IsSubItem = true,
                    Unit = l.Unit,
                    CustomerItemNumber = l.GetUserField(UnicontaUserFields.ExternalSku) as string
                });
            }

            yield return po;
        }
    }

    // Server-side filter window. Any order whose UpdatedAt is within this many minutes will be
    // returned by the eligibility query. Window must be wide enough that an order which fails JD
    // validation and is re-clicked by the user still re-appears within the window. 30 min is
    // generous — typical click-to-sync flow is sub-minute, and a Fejlet re-click bumps UpdatedAt
    // server-side, so the window resets on user action.
    private static readonly TimeSpan SalesOrderRecentWindow = TimeSpan.FromMinutes(30);

    public async IAsyncEnumerable<LocalSalesOrder> ReadAllSalesOrdersAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var queryApi = await connectionManager.CreateQueryApiAsync();

        // Filter server-side on UpdatedAt rather than pulling every sales order and filtering in
        // memory. Two reasons:
        //   1. Smaller payload (only recently-touched rows), so the tick is faster and cheaper.
        //   2. The unfiltered path in `QueryAPI.Query<T>(null)` opts into `SetCache=true` which
        //      participates in the SDK's session-level cache machinery — observed (2026-05-27) to
        //      return server-cached snapshots that miss recent UI-side edits. A filtered query
        //      takes the simpler code path (SetCache=false) and re-asks the server each tick.
        var cutoff = DateTime.UtcNow - SalesOrderRecentWindow;
        var recentFilter = new[]
        {
            PropValuePair.GenereteWhereElements("UpdatedAt", cutoff, CompareOperator.GreaterThanOrEqual, typeof(DateTime))
        };

        var querySw = System.Diagnostics.Stopwatch.StartNew();
        var orders = await queryApi.Query<DebtorOrderClient>(recentFilter);
        querySw.Stop();

        // Diagnostic: lets us separate "Uniconta returned 0 rows" from "Uniconta returned N rows
        // but none passed the eligibility filter". App Insights only — not integration_logs — to
        // keep noise low. If we suspect server-side staleness in the future, this is the row to
        // check (raw count vs. eligible count vs. session healthy).
        _logger.LogInformation(
            "Uniconta Query<DebtorOrderClient> updated_since={Cutoff:o}: raw_count={Count} query_ms={QueryMs} session_logged_in={Logged}",
            cutoff, orders?.Length ?? -1, querySw.ElapsedMilliseconds, queryApi.LoggedIn);

        // An empty Group means the order has not yet been pushed to JD; "Fejlet" means a previous push
        // failed and is parked until a user re-sets Xoverfor1. On success SyncSalesOrdersToJd sets Group =
        // "Oprettet", and SyncRequestOrderStatusToUniconta later replaces it with the live JD status —
        // either way the order then stops being re-processed (and re-PDF'd) here.
        var filteredOrders = (orders ?? Enumerable.Empty<DebtorOrderClient>())
            .Where(o => o != null
                && o.GetUserFieldBoolean(UnicontaUserFields.SalesOrderTransferFlag)
                && (string.IsNullOrWhiteSpace(o.Group) || string.Equals(o.Group, SalesOrderJdGroup.Failed, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (filteredOrders.Count == 0)
            yield break;

        // Load master data once up front instead of one query per order / per item (avoids N+1 traffic to Uniconta).
        var debtors = await queryApi.Query<DebtorClient>((IEnumerable<PropValuePair>?)null);
        var debtorsByAccount = (debtors ?? Enumerable.Empty<DebtorClient>())
            .Where(d => !string.IsNullOrEmpty(d?.Account))
            .GroupBy(d => d!.Account!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var items = await queryApi.Query<InvItemClient>((IEnumerable<PropValuePair>?)null);
        var itemTypeBySku = (items ?? Enumerable.Empty<InvItemClient>())
            .Where(i => !string.IsNullOrEmpty(i?.Item))
            .GroupBy(i => i!.Item!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (int)g.First()._ItemType, StringComparer.OrdinalIgnoreCase);

        // Bulk Master/Detail: one round-trip for ALL pending orders' lines instead of one per order.
        // Previously this loop did `Query<DebtorOrderLineClient>(new List{o}, null)` per order which
        // dominated the per-tick latency once more than a handful of orders were pending.
        var allMasters = filteredOrders.Cast<UnicontaBaseEntity>().ToList();
        var allLines = await queryApi.Query<DebtorOrderLineClient>(allMasters, null);
        // DebtorOrderLineClient._OrderRowId is the FK back to DebtorOrderClient.RowId.
        var linesByOrderRowId = (allLines ?? Enumerable.Empty<DebtorOrderLineClient>())
            .Where(l => l != null)
            .GroupBy(l => l.OrderRowId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var o in filteredOrders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            debtorsByAccount.TryGetValue(o.Account ?? string.Empty, out var debtor);

            var so = new LocalSalesOrder
            {
                OrderNumber = o.OrderNumber,
                Date = o._Created,
                DebtorAccount = o.Account,
                Comments = o._Remark,
                DeliveryName = o._DeliveryName ?? debtor?._Name,
                DeliveryAddress1 = o._DeliveryAddress1 ?? debtor?._Address1,
                DeliveryAddress2 = o._DeliveryAddress2 ?? debtor?._Address2,
                DeliveryAddress3 = o._DeliveryAddress3 ?? debtor?._Address3,
                DeliveryZip = o._DeliveryZipCode ?? debtor?._ZipCode,
                DeliveryCity = o._DeliveryCity ?? debtor?._City,
                DeliveryCountryCode = debtor?._Country == 0 ? "DK" : debtor?._Country.ToString(),
                // Additional fields for JD mapping
                DeliveryDate = o._DeliveryDate == default ? null : o._DeliveryDate,
                TrackingNote = o.GetUserField(UnicontaUserFields.TrackingNote) as string,
                DeliveryNoteText = o.GetUserField(UnicontaUserFields.DeliveryNoteText) as string,
                RemarkText = o.GetUserField(UnicontaUserFields.Remark) as string,
                DeliveryType = o.GetUserField(UnicontaUserFields.DeliveryType) as string,
                DeliveryContactEmail = o.DeliveryContactEmail,
                DeliveryContactPhone = o.DeliveryPhone,
                // Shipmondo-related fields
                TransportType = o.GetUserField(UnicontaUserFields.TransportType) as string,
                DeliveryTime = o.GetUserField(UnicontaUserFields.DeliveryTime) as DateTime?,
                CarrierMessage = o.GetUserField(UnicontaUserFields.CarrierMessage) as string,

                DebtorName = debtor?._Name,
                DebtorCVR = debtor?.CompanyRegNo,
                YourReference = o.YourRef,
                OurReference = o.OurRef,
            };

            if (linesByOrderRowId.TryGetValue(o.RowId, out var lines))
            {
                foreach (var l in lines)
                {
                    int itemType = 0;
                    if (!string.IsNullOrEmpty(l._Item) && itemTypeBySku.TryGetValue(l._Item, out var cachedType))
                    {
                        itemType = cachedType;
                    }

                    so.Lines.Add(new LocalSalesOrderLine
                    {
                        Sku = l._Item,
                        ItemName = l.Text,
                        Quantity = l._Qty,
                        Unit = l.Unit,
                        Price = (decimal?)l.Price,
                        ItemType = itemType
                    });
                }
            }

            yield return so;
        }
    }

    public async IAsyncEnumerable<DebtorDeliveryNoteInfo> ReadDebtorDeliveryNotesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // TODO: Implement proper DebtorDeliveryNote fetching from Uniconta
        // This is a placeholder implementation that can be enhanced once we have access to
        // the actual Uniconta API documentation and understand the correct way to access file data

        // For now, return an empty collection to avoid compilation errors
        // This method should be implemented to:
        // 1. Query DebtorDeliveryNoteClient from Uniconta
        // 2. Extract file data from the notes
        // 3. Return DebtorDeliveryNoteInfo objects with file information

        yield break; // Return empty collection
    }

    public async IAsyncEnumerable<LocalSalesOrder> ReadSalesOrdersWithGroupAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var queryApi = await connectionManager.CreateQueryApiAsync();
        // Fetch all orders - we need to scan them to match with JD
        var orders = await queryApi.Query<DebtorOrderClient>((IEnumerable<PropValuePair>?)null);

        foreach (var o in (orders ?? Enumerable.Empty<DebtorOrderClient>()).Where(o => o != null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new LocalSalesOrder
            {
                OrderNumber = o.OrderNumber,
                Group = o.Group,
                // Minimal fields required for matching
            };
        }
    }

    public Task<bool> UpdateSalesOrderGroupAsync(int orderNumber, string group, CancellationToken cancellationToken)
        => connectionManager.ExecuteWithRetryAsync(async () =>
        {
            var queryApi = await connectionManager.CreateQueryApiAsync();
            var crudApi = await connectionManager.CreateCrudApiAsync();

            // 1. Find the order
            var filter = new[] { PropValuePair.GenereteWhereElements("OrderNumber", typeof(int), orderNumber.ToString()) };
            var orders = await queryApi.Query<DebtorOrderClient>(filter);
            var order = (orders ?? Enumerable.Empty<DebtorOrderClient>()).FirstOrDefault();

            if (order == null)
            {
                _logger.LogWarning("Could not find sales order {OrderNumber} in Uniconta for status update", orderNumber);
                return false;
            }

            // 2. Update the group
            // If the group is the same, we don't need to update, but the logic calling this should handle that check.
            // However, checking again is safe.
            if (string.Equals(order.Group, group, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Set both the property and the backing field — Update(order) only persists the change
            // when the raw _Group field is updated.
            order.Group = group;
            order._Group = group;
            var result = await crudApi.Update(order);

            if (result != ErrorCodes.Succes)
            {
                _logger.LogError("Failed to update status group for order {OrderNumber}. Uniconta Error: {Error}", orderNumber, result);
                return false;
            }

            return true;
        });

    public Task<bool> SetSalesOrderStatusAsync(int orderNumber, string group, IReadOnlyDictionary<string, object> userFields, CancellationToken cancellationToken)
        => connectionManager.ExecuteWithRetryAsync(async () =>
        {
            var queryApi = await connectionManager.CreateQueryApiAsync();
            var crudApi = await connectionManager.CreateCrudApiAsync();

            var filter = new[] { PropValuePair.GenereteWhereElements("OrderNumber", typeof(int), orderNumber.ToString()) };
            var orders = await queryApi.Query<DebtorOrderClient>(filter);
            var order = (orders ?? Enumerable.Empty<DebtorOrderClient>()).FirstOrDefault();

            if (order == null)
            {
                _logger.LogWarning("Could not find sales order {OrderNumber} in Uniconta for status update", orderNumber);
                return false;
            }

            // Set both the property and the backing field for Group — Update(order) only persists the
            // change when the raw _Group field is updated.
            order.Group = group;
            order._Group = group;
            foreach (var (name, value) in userFields)
                order.SetUserField(name, value);

            var result = await crudApi.Update(order);

            if (result != ErrorCodes.Succes)
            {
                _logger.LogError("Failed to update status (group={Group}) on sales order {OrderNumber}. Uniconta Error: {Error}", group, orderNumber, result);
                return false;
            }

            return true;
        });

    public Task<bool> UpdatePurchaseOrderLineQuantityAsync(int purchaseNumber, string sku, double qtyNow, CancellationToken cancellationToken)
        => connectionManager.ExecuteWithRetryAsync(async () =>
        {
            var queryApi = await connectionManager.CreateQueryApiAsync();
            var crudApi = await connectionManager.CreateCrudApiAsync();

            // 1. Find the purchase order
            var filter = new[] { PropValuePair.GenereteWhereElements("OrderNumber", typeof(int), purchaseNumber.ToString()) };
            var orders = await queryApi.Query<CreditorOrderClient>(filter);
            var order = (orders ?? Enumerable.Empty<CreditorOrderClient>()).FirstOrDefault();

            if (order == null)
            {
                _logger.LogWarning("Could not find purchase order {OrderNumber} in Uniconta for quantity update", purchaseNumber);
                return false;
            }

            // 2. Find the line
            var masters = new List<UnicontaBaseEntity> { order };
            var lines = await queryApi.Query<CreditorOrderLineClient>(masters, null);
            var line = (lines ?? Enumerable.Empty<CreditorOrderLineClient>()).FirstOrDefault(l => string.Equals(l._Item, sku, StringComparison.OrdinalIgnoreCase));

            if (line == null)
            {
                _logger.LogWarning("Could not find line with SKU {Sku} in purchase order {OrderNumber}", sku, purchaseNumber);
                return false;
            }

            // 3. Update the quantity
            // If the quantity is already correct, no need to update
            if (Math.Abs(line._Qty - qtyNow) < 0.001)
            {
                return true;
            }

            line._QtyNow = qtyNow;
            var result = await crudApi.Update(line);

            if (result != ErrorCodes.Succes)
            {
                _logger.LogError("Failed to update quantity for line {Sku} in purchase order {OrderNumber}. Uniconta Error: {Error}", sku, purchaseNumber, result);
                return false;
            }

            return true;
        });

    public Task<bool> SetPurchaseOrderHeaderFieldAsync(int purchaseNumber, string fieldName, object value, CancellationToken cancellationToken)
        => SetPurchaseOrderHeaderFieldsAsync(purchaseNumber, new Dictionary<string, object> { [fieldName] = value }, cancellationToken);

    public Task<bool> SetPurchaseOrderHeaderFieldsAsync(int purchaseNumber, IReadOnlyDictionary<string, object> fields, CancellationToken cancellationToken)
        => connectionManager.ExecuteWithRetryAsync(async () =>
        {
            var queryApi = await connectionManager.CreateQueryApiAsync();
            var crudApi = await connectionManager.CreateCrudApiAsync();

            var filter = new[] { PropValuePair.GenereteWhereElements("OrderNumber", typeof(int), purchaseNumber.ToString()) };
            var orders = await queryApi.Query<CreditorOrderClient>(filter);
            var order = (orders ?? Enumerable.Empty<CreditorOrderClient>()).FirstOrDefault();

            if (order == null)
            {
                _logger.LogWarning("Could not find purchase order {OrderNumber} in Uniconta for header update", purchaseNumber);
                return false;
            }

            foreach (var (name, value) in fields)
                order.SetUserField(name, value);

            var result = await crudApi.Update(order);

            if (result != ErrorCodes.Succes)
            {
                _logger.LogError("Failed to update fields [{Fields}] on purchase order {OrderNumber}. Uniconta Error: {Error}", string.Join(", ", fields.Keys), purchaseNumber, result);
                return false;
            }

            return true;
        });

    // How far back the posted-invoice safety-net scans. A server-side filter on the invoice Date is
    // used (not an in-memory scan of everything) for the same two reasons the sales-order query filters
    // on UpdatedAt: smaller payload, and — critically — the unfiltered Query<T>(null) path opts into
    // SetCache=true which can return a stale (even empty) session-cached snapshot; a filtered query
    // takes the SetCache=false path and re-asks the server each tick. Re-sends inside the window are
    // free because JD dedupes on the "PO {n}" text, so the window errs wide.
    private static readonly TimeSpan PostedInvoiceRecentWindow = TimeSpan.FromDays(7);

    // Invoice-line group that denotes a physical stock item. JD is a warehouse and only receives real
    // stock, so fee/charge lines (group "Charges") are skipped — otherwise a non-catalog fee line would
    // make JD reject the whole shipment.
    private const string StockLineGroup = "Stock";

    public async IAsyncEnumerable<LocalPurchaseInvoice> ReadPostedPurchaseInvoicesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var queryApi = await connectionManager.CreateQueryApiAsync();

        // CreditorInvoiceClient.Date is the accounting/posting date (a calendar date in the company's
        // local time), not a UTC timestamp. We compare against a UTC-derived cutoff, so there is a
        // sub-day timezone skew — harmless here because the window is deliberately wide (days) and JD
        // dedupe makes any re-send inside the window free.
        var cutoff = DateTime.UtcNow.Date - PostedInvoiceRecentWindow;
        var recentFilter = new[]
        {
            PropValuePair.GenereteWhereElements("Date", cutoff, CompareOperator.GreaterThanOrEqual, typeof(DateTime))
        };

        var invoices = await queryApi.Query<CreditorInvoiceClient>(recentFilter);

        // Opt-in via the same manual flag as the open-order flow (a user still sets "Overfør til JD"
        // on the booked invoice), never sent yet, and with a usable dedup identity (order number).
        foreach (var inv in (invoices ?? Enumerable.Empty<CreditorInvoiceClient>())
                     .Where(i => i != null
                                 && !i._Deleted
                                 && i.OrderNumber > 0
                                 && i.GetUserFieldBoolean(UnicontaUserFields.PurchaseOrderTransferFlag)
                                 && PurchaseOrderJdStatusValues.IsPending(i.GetUserField(UnicontaUserFields.PurchaseOrderJdStatus) as string)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var invoice = new LocalPurchaseInvoice
            {
                PurchaseNumber = inv.OrderNumber,
                InvoiceNumber = inv.InvoiceNumber,
                Date = inv.Date,
                SupplierAccount = inv.Account,
            };

            var masters = new List<UnicontaBaseEntity> { inv };
            var lines = await queryApi.Query<CreditorInvoiceLines>(masters, null);
            foreach (var l in lines ?? Enumerable.Empty<CreditorInvoiceLines>())
            {
                // Only physical stock lines go to JD; skip fee/charge lines and blank items.
                if (string.IsNullOrWhiteSpace(l.Item)) continue;
                if (!string.Equals(l.Group, StockLineGroup, StringComparison.OrdinalIgnoreCase)) continue;

                invoice.Lines.Add(new LocalPurchaseInvoiceLine
                {
                    Sku = l.Item,
                    Quantity = l.Qty,
                    IsSubItem = true,
                    Unit = l.Unit,
                    CustomerItemNumber = l.GetUserField(UnicontaUserFields.ExternalSku) as string
                });
            }

            // A flagged invoice with no stock lines (e.g. a pure fee/charge invoice flagged by mistake)
            // would otherwise map to a JD shipment with zero lines. Skip it — never send an empty shipment.
            if (invoice.Lines.Count == 0)
            {
                _logger.LogInformation(
                    "Posted invoice {Invoice} (order {Order}) is flagged for JD but has no stock lines after filtering; skipping.",
                    inv.InvoiceNumber, inv.OrderNumber);
                continue;
            }

            yield return invoice;
        }
    }

    public Task<bool> SetPurchaseInvoiceHeaderFieldsAsync(int orderNumber, IReadOnlyDictionary<string, object> fields, CancellationToken cancellationToken)
        => connectionManager.ExecuteWithRetryAsync(async () =>
        {
            var queryApi = await connectionManager.CreateQueryApiAsync();
            var crudApi = await connectionManager.CreateCrudApiAsync();

            var filter = new[] { PropValuePair.GenereteWhereElements("OrderNumber", typeof(int), orderNumber.ToString()) };
            var invoices = await queryApi.Query<CreditorInvoiceClient>(filter);
            var matches = (invoices ?? Enumerable.Empty<CreditorInvoiceClient>()).Where(i => i != null && !i._Deleted).ToList();

            if (matches.Count == 0)
            {
                _logger.LogWarning("Could not find posted purchase invoice for order {OrderNumber} in Uniconta for header update", orderNumber);
                return false;
            }

            // Update every posted invoice for this order so none stays flagged. Best-effort: any
            // failure is surfaced to the caller, which does not depend on it for idempotency (JD dedup does).
            var allOk = true;
            foreach (var inv in matches)
            {
                foreach (var (name, value) in fields)
                    inv.SetUserField(name, value);

                var result = await crudApi.Update(inv);
                if (result != ErrorCodes.Succes)
                {
                    allOk = false;
                    _logger.LogWarning("Failed to update fields [{Fields}] on posted invoice {Invoice} (order {OrderNumber}). Uniconta Error: {Error}",
                        string.Join(", ", fields.Keys), inv.InvoiceNumber, orderNumber, result);
                }
            }

            return allOk;
        });
}


