namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Models;

/// <summary>
/// A posted purchase invoice (Uniconta CreditorInvoiceClient) that carries the JD transfer flag.
/// Used by the safety-net sync that catches purchase orders which were booked before a user
/// clicked "Overfør til JD" on the open order. <see cref="PurchaseNumber"/> is the originating
/// purchase-order number the invoice retains — it is the dedup identity shared with the open-order
/// flow (both emit "PO {PurchaseNumber}" as the JD incoming-shipment text).
/// </summary>
public sealed class LocalPurchaseInvoice
{
    public int PurchaseNumber { get; init; }
    public long InvoiceNumber { get; init; }
    public DateTime? Date { get; init; }
    public string? SupplierAccount { get; init; }

    // Header fields copied onto the booked invoice from the originating purchase order's user fields
    // (xCarrier / xRemarksForJD / xEnhedstype / xAntalEnheder). Carried so the safety-net produces the
    // exact same JD incoming shipment as the open-order path — see PurchaseOrderMapper.BuildIncomingShipment.
    public string? Carrier { get; init; }
    public string? RemarkText { get; init; }
    public string? ContainerType { get; init; }
    public double? ContainerCount { get; init; }

    public List<LocalPurchaseInvoiceLine> Lines { get; init; } = new();
}

public sealed class LocalPurchaseInvoiceLine
{
    public string Sku { get; init; } = string.Empty;
    public double Quantity { get; init; }
    // isSubItem is derived in the mapper from whether a container parent is emitted (mirrors the
    // open-order path); it is intentionally NOT a per-line field here.
    public string? Unit { get; init; }
    public string? CustomerItemNumber { get; init; }
}
