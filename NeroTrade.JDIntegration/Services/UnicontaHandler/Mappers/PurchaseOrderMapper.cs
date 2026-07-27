namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;

using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;

public sealed class PurchaseOrderMapper
{
    public JdIncomingShipmentCreate Map(LocalPurchaseOrder po) =>
        BuildIncomingShipment(
            po.PurchaseNumber,
            po.DeliveryDate,
            po.RemarkText,
            po.Carrier,
            po.ContainerType,
            po.ContainerCount,
            po.Lines.Select(l => new LineInput(l.Sku, l.Quantity, l.Unit, l.CustomerItemNumber)));

    /// <summary>
    /// Maps a posted purchase invoice to a JD incoming shipment. Delegates to the SAME
    /// <see cref="BuildIncomingShipment"/> core as <see cref="Map(LocalPurchaseOrder)"/>, so the safety-net
    /// produces an identical shipment (carrier, container/"kolli" parent, "PO {n}[ - remark]" identity,
    /// notification/approval fields) — the two paths cannot drift. The shared "PO {originatingOrderNumber}"
    /// text is what JD's existing-shipment dedup uses to treat an order already sent via the open-order
    /// flow (or an earlier tick) as a duplicate and skip it.
    /// </summary>
    public JdIncomingShipmentCreate Map(LocalPurchaseInvoice invoice) =>
        BuildIncomingShipment(
            invoice.PurchaseNumber,
            deliveryDate: null, // posted invoices carry no expected-delivery date; same fallback as the PO path
            invoice.RemarkText,
            invoice.Carrier,
            invoice.ContainerType,
            invoice.ContainerCount,
            invoice.Lines.Select(l => new LineInput(l.Sku, l.Quantity, l.Unit, l.CustomerItemNumber)));

    // Single source of truth for the Uniconta purchase → JD incoming-shipment structure. Both the
    // open-order and posted-invoice (safety-net) paths funnel through here so they are identical by
    // construction.
    private static JdIncomingShipmentCreate BuildIncomingShipment(
        int purchaseNumber,
        DateTime? deliveryDate,
        string? remarkText,
        string? carrier,
        string? containerType,
        double? containerCount,
        IEnumerable<LineInput> lines)
    {
        var create = new JdIncomingShipmentCreate
        {
            date = deliveryDate ?? DateTime.UtcNow.AddDays(2),
            // "PO {n}" is the machine key parsed back out via JdOrderHelper (dedup and
            // received-quantity sync) — the remark may only ever be appended after it.
            text = string.IsNullOrWhiteSpace(remarkText)
                ? $"PO {purchaseNumber}"
                : $"PO {purchaseNumber} - {remarkText.Trim()}",
            SourcePurchaseNumber = purchaseNumber,
            carrier = string.IsNullOrWhiteSpace(carrier) ? "TBD" : carrier.Trim(),
            notificationEmails = "mb@nerotrade.dk",
            disableApprovalEmail = false,
            files = []
        };

        // JD expects pallet/container shipments as a parent (isSubItem=false, the container) with the
        // products as children (isSubItem=true). When the Lagerhotel container fields are set we emit
        // that parent here; otherwise the products go as a flat list (isSubItem=false) — both are valid
        // JD structures. The previous code hardcoded isSubItem=true on every line with no parent, which
        // left the pallet structure malformed on the incoming shipment.
        var hasContainer = !string.IsNullOrWhiteSpace(containerType) && containerCount is > 0;
        if (hasContainer)
        {
            // Pure container parent: no SKU/catalog. unit carries the container-type name so
            // JdLogisticsService.SetContainerTypesAsync resolves its inventoryContainerType, and
            // ResolveCatalogItems skips it (no SKU). It is added first so it precedes its children.
            create.lines.Add(new JdIncomingLine
            {
                quantity = (int)Math.Round(containerCount!.Value),
                isSubItem = false,
                Sku = null,
                // xEnhedstype is free text already typed in Danish ("Palle"), so the translation is a
                // no-op here — it only kicks in if someone types the Uniconta enum name instead.
                unit = UnitTranslator.ToJdContainerTypeName(containerType)
            });
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Sku)) continue;
            create.lines.Add(new JdIncomingLine
            {
                quantity = (int)Math.Round(line.Quantity),
                isSubItem = hasContainer, // child of the container parent, or flat (false) when none
                externalIdentification = string.IsNullOrWhiteSpace(line.CustomerItemNumber)
                    ? null
                    : line.CustomerItemNumber,
                Sku = line.Sku,
                // catalog is intentionally left null here. JD matches a line to a catalog item
                // solely via catalog.id, so it is resolved against JD's catalog in
                // JdLogisticsService before the shipment is sent. A line that cannot be resolved
                // must fail loudly rather than be sent with a bogus id (JD would register "Ukendt").
                //
                // Uniconta hands us the English ItemUnit enum name ("Packages"), JD names its container
                // types in Danish ("Kolli") — translate here so the downstream name match can succeed.
                // See UnitTranslator.
                unit = UnitTranslator.ToJdContainerTypeName(line.Unit)
            });
        }
        return create;
    }

    // Common line shape both purchase-order and posted-invoice lines project into before mapping.
    private readonly record struct LineInput(string? Sku, double Quantity, string? Unit, string? CustomerItemNumber);
}


