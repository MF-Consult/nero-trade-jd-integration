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
                externalIdentification = line.Sku,
                catalog = new JdIncomingShipmentCatalogRef { id = 0 },
                unit = line.Unit
            });
        }
        return create;
    }
}


