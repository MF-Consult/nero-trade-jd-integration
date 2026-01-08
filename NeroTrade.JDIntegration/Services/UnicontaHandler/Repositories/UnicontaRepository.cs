using Uniconta.DataModel;

namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Repositories;

using Models;
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
        foreach (var d in debtors.Where(d => d.GetUserFieldBoolean("Xoverfort")))
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
        foreach (var i in items.Where(i => i.GetUserFieldBoolean("XoverforVare")))
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
        foreach (var o in orders)
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
                    Unit = l.Unit
                });
            }

            yield return po;
        }
    }

    public async IAsyncEnumerable<LocalSalesOrder> ReadAllSalesOrdersAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var queryApi = await connectionManager.CreateQueryApiAsync();
        
        var orders = await queryApi.Query<DebtorOrderClient>((IEnumerable<PropValuePair>?)null);
        foreach (var o in orders.Where(o => o.GetUserFieldBoolean("Xoverfor1")))
        {
            //Get a single Customer by sending a filter query to Uniconta 
            List<PropValuePair> filter = [PropValuePair.GenereteWhereElements("Account", typeof(string), o.Account)]; 
            var debtor = await queryApi.Query<DebtorClient>(filter); 

            cancellationToken.ThrowIfCancellationRequested();
            var so = new LocalSalesOrder
            {
                OrderNumber = o.OrderNumber,
                Date = o._Created,
                DebtorAccount = o.Account,
                Comments = o._Remark,
                DeliveryName = o._DeliveryName ?? debtor.FirstOrDefault()?._Name,
                DeliveryAddress1 = o._DeliveryAddress1 ?? debtor.FirstOrDefault()?._Address1,
                DeliveryAddress2 = o._DeliveryAddress2 ?? debtor.FirstOrDefault()?._Address2,
                DeliveryAddress3 = o._DeliveryAddress3 ?? debtor.FirstOrDefault()?._Address3,
                DeliveryZip = o._DeliveryZipCode ?? debtor.FirstOrDefault()?._ZipCode,
                DeliveryCity = o._DeliveryCity ?? debtor.FirstOrDefault()?._City,
                DeliveryCountryCode = debtor.FirstOrDefault()?._Country == 0 ? "DK" : debtor.FirstOrDefault()?._Country.ToString(),
                // Additional fields for JD mapping
                DeliveryDate = o.GetUserField("xUdleveringsdato") as DateTime?,
                TrackingNote = o.GetUserField("xSporingsnote") as string,
                DeliveryNoteText = o.GetUserField("xNoteflgseddel") as string,
                RemarkText = o.GetUserField("xBemaerk") as string,
                DeliveryType = o.GetUserField("xDeliveryType") as string,
                DeliveryContactEmail = o.DeliveryContactEmail,
                DeliveryContactPhone = o.DeliveryPhone,
                // Shipmondo-related fields
                TransportType = o.GetUserField("xTransporttype") as string,
                DeliveryTime = o.GetUserField("xtidspunkt") as DateTime?,
                CarrierMessage = o.GetUserField("xbesked") as string,

                DebtorName = debtor.FirstOrDefault()?._Name,
                DebtorCVR = debtor.FirstOrDefault()?.CompanyRegNo,
                YourReference = o.YourRef,
                OurReference = o.OurRef, 
            };

            var masters = new List<UnicontaBaseEntity> { o };
            var lines = await queryApi.Query<DebtorOrderLineClient>(masters, null);
            foreach (var l in lines ?? Enumerable.Empty<DebtorOrderLineClient>())
            {
                so.Lines.Add(new LocalSalesOrderLine
                {
                    Sku = l._Item,
                    ItemName = l.Text,  
                    Quantity = l._Qty,
                    Unit = l.Unit,
                    Price = (decimal?)l.Price 
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
        
        foreach (var o in orders)
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

    public async Task<bool> UpdateSalesOrderGroupAsync(int orderNumber, string group, CancellationToken cancellationToken)
    {
        var queryApi = await connectionManager.CreateQueryApiAsync();
        var crudApi = await connectionManager.CreateCrudApiAsync();

        // 1. Find the order
        var filter = new[] { PropValuePair.GenereteWhereElements("OrderNumber", typeof(int), orderNumber.ToString()) };
        var orders = await queryApi.Query<DebtorOrderClient>(filter);
        var order = orders.FirstOrDefault();

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

        order.Group = group;
        var result = await crudApi.Update(order);

        if (result != ErrorCodes.Succes)
        {
            _logger.LogError("Failed to update status group for order {OrderNumber}. Uniconta Error: {Error}", orderNumber, result);
            return false;
        }

        return true;
    }
}


