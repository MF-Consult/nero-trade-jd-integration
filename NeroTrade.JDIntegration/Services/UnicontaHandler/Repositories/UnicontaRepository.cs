using Uniconta.DataModel;

namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Repositories;

using Models;
using Constants;
using Uniconta.API.System;
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

    public async IAsyncEnumerable<LocalSalesOrder> ReadAllSalesOrdersAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var queryApi = await connectionManager.CreateQueryApiAsync();

        var orders = await queryApi.Query<DebtorOrderClient>((IEnumerable<PropValuePair>?)null);
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
                DeliveryDate = o.GetUserField(UnicontaUserFields.DeliveryDate) as DateTime?,
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

            var masters = new List<UnicontaBaseEntity> { o };
            var lines = await queryApi.Query<DebtorOrderLineClient>(masters, null);
            foreach (var l in lines ?? Enumerable.Empty<DebtorOrderLineClient>())
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

            // Update against a snapshot of the loaded state so Uniconta only persists the changed field
            // (a plain Update(order) does not reliably persist the change here).
            var original = StreamingManager.Clone(order);
            order.Group = group;
            var result = await crudApi.Update(order, original);

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

            // Update against a snapshot of the loaded state so Uniconta persists the changes.
            var original = StreamingManager.Clone(order);
            order.Group = group;
            foreach (var (name, value) in userFields)
                order.SetUserField(name, value);

            var result = await crudApi.Update(order, original);

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

            var original = StreamingManager.Clone(line);
            line._QtyNow = qtyNow;
            var result = await crudApi.Update(line, original);

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

            // Update against a snapshot of the loaded state so Uniconta persists the change.
            var original = StreamingManager.Clone(order);
            foreach (var (name, value) in fields)
                order.SetUserField(name, value);

            var result = await crudApi.Update(order, original);

            if (result != ErrorCodes.Succes)
            {
                _logger.LogError("Failed to update fields [{Fields}] on purchase order {OrderNumber}. Uniconta Error: {Error}", string.Join(", ", fields.Keys), purchaseNumber, result);
                return false;
            }

            return true;
        });
}


