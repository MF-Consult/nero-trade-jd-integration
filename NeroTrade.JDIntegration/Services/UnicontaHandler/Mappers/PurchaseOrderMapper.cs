namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;

using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;

public sealed class PurchaseOrderMapper
{
    public JdIncomingShipmentCreate Map(LocalPurchaseOrder po)
    {
        var create = new JdIncomingShipmentCreate
        {
            date = DateTime.UtcNow.AddDays(2),
            text = $"PO {po.PurchaseNumber}",
            SourcePurchaseNumber = po.PurchaseNumber,
            carrier = "TBD",
            notificationEmails = null,
            disableApprovalEmail = true,
            files = []
        };
        foreach (var line in po.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Sku)) continue;
            create.lines.Add(new JdIncomingLine
            {
                quantity = (int)Math.Round(line.Quantity),
                isSubItem = line.IsSubItem,
                externalIdentification = string.IsNullOrWhiteSpace(line.CustomerItemNumber)
                    ? null
                    : line.CustomerItemNumber,
                Sku = line.Sku,
                // catalog is intentionally left null here. JD matches a line to a catalog item
                // solely via catalog.id, so it is resolved against JD's catalog in
                // JdLogisticsService before the shipment is sent. A line that cannot be resolved
                // must fail loudly rather than be sent with a bogus id (JD would register "Ukendt").
                unit = line.Unit
            });
        }
        return create;
    }

    /// <summary>
    /// Maps a posted purchase invoice to a JD incoming shipment. Emits the SAME "PO {originatingOrderNumber}"
    /// identity as <see cref="Map(LocalPurchaseOrder)"/> so JD's existing-shipment dedup treats an order
    /// already sent via the open-order flow (or an earlier tick) as a duplicate and skips it.
    /// </summary>
    public JdIncomingShipmentCreate Map(LocalPurchaseInvoice invoice)
    {
        var create = new JdIncomingShipmentCreate
        {
            date = DateTime.UtcNow.AddDays(2),
            text = $"PO {invoice.PurchaseNumber}",
            SourcePurchaseNumber = invoice.PurchaseNumber,
            carrier = "TBD",
            notificationEmails = null,
            disableApprovalEmail = true,
            files = []
        };
        foreach (var line in invoice.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Sku)) continue;
            create.lines.Add(new JdIncomingLine
            {
                quantity = (int)Math.Round(line.Quantity),
                isSubItem = line.IsSubItem,
                externalIdentification = string.IsNullOrWhiteSpace(line.CustomerItemNumber)
                    ? null
                    : line.CustomerItemNumber,
                Sku = line.Sku,
                unit = line.Unit
            });
        }
        return create;
    }
}


