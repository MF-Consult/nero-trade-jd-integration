namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;

using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;

public sealed class PurchaseOrderMapper
{
    public JdIncomingShipmentCreate Map(LocalPurchaseOrder po)
    {
        var create = new JdIncomingShipmentCreate
        {
            date = po.DeliveryDate ?? DateTime.UtcNow.AddDays(2),
            // "PO {n}" is the machine key parsed back out via JdOrderHelper (dedup and
            // received-quantity sync) — the remark may only ever be appended after it.
            text = string.IsNullOrWhiteSpace(po.RemarkText)
                ? $"PO {po.PurchaseNumber}"
                : $"PO {po.PurchaseNumber} - {po.RemarkText.Trim()}",
            SourcePurchaseNumber = po.PurchaseNumber,
            carrier = string.IsNullOrWhiteSpace(po.Carrier) ? "TBD" : po.Carrier!.Trim(),
            notificationEmails = "mb@nerotrade.dk",
            disableApprovalEmail = false,
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
}


