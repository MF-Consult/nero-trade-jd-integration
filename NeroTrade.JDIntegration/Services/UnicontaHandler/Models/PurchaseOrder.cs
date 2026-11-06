namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Models;

public sealed class LocalPurchaseOrder
{
    public int PurchaseNumber { get; init; }
    public DateTime? Date { get; init; }

    /// <summary>
    /// Uniconta's last-modified time for the order header. A user ticking <c>xTransferToJD</c> bumps it, so it is
    /// our best proxy for "when the user pressed send" and lets the sync log the real end-to-end delay.
    /// </summary>
    public DateTime? UpdatedAt { get; init; }
    public string? SupplierAccount { get; init; }
    public string? OurRef { get; init; }
    public string? YourRef { get; init; }
    public string? Requisition { get; init; }
    public string? Comments { get; init; }
    public string? DeliveryName { get; init; }
    public string? DeliveryAddress1 { get; init; }
    public string? DeliveryAddress2 { get; init; }
    public string? DeliveryAddress3 { get; init; }
    public string? DeliveryZip { get; init; }
    public string? DeliveryCity { get; init; }
    public string? DeliveryCountryCode { get; init; }

    // Additional fields for JD mapping
    public DateTime? DeliveryDate { get; init; }
    public string? TrackingNote { get; init; }
    public string? DeliveryNoteText { get; init; }
    public string? RemarkText { get; init; }
    public string? Carrier { get; init; }

    // Lagerhotel: container type (Palle/Container) + count → a JD parent line (a pure container,
    // isSubItem=false) that the product lines hang under as sub-items. Both must be set for a parent
    // to be emitted; otherwise the product lines are sent as a flat list.
    public string? ContainerType { get; init; }
    public double? ContainerCount { get; init; }

    // Shipmondo-related fields
    public string? TransportType { get; init; }
    public DateTime? DeliveryTime { get; init; }
    public string? CarrierMessage { get; init; }

    public List<LocalPurchaseOrderLine> Lines { get; init; } = new();
}

public sealed class LocalPurchaseOrderLine
{
    public string Sku { get; init; } = string.Empty;
    public double Quantity { get; init; }
    public string? Unit { get; init; }
    public string? CustomerItemNumber { get; init; }
}


