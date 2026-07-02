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
    public List<LocalPurchaseInvoiceLine> Lines { get; init; } = new();
}

public sealed class LocalPurchaseInvoiceLine
{
    public string Sku { get; init; } = string.Empty;
    public double Quantity { get; init; }
    public bool IsSubItem { get; init; }
    public string? Unit { get; init; }
    public string? CustomerItemNumber { get; init; }
}
