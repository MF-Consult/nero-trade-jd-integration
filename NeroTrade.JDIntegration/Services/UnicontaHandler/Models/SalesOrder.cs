namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Models;

public sealed class LocalSalesOrder
{
    public int OrderNumber { get; init; }
    public DateTime? Date { get; init; }
    public string? DebtorAccount { get; init; }
    public string? DebtorName { get; init; }
    public string? DebtorCVR { get; init; }
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
    public string? DeliveryType { get; init; } // "GLS" or "Palle Fragt"
    public string? DeliveryContactEmail { get; init; }
    public string? DeliveryContactPhone { get; init; }
    
    // Reference fields
    public string? YourReference { get; init; }
    public string? OurReference { get; init; }

    // Shipmondo-related fields
    public string? TransportType { get; init; }
    public DateTime? DeliveryTime { get; init; }
    public string? CarrierMessage { get; init; }

    // Status mapping
    public string? Group { get; init; }

    public List<LocalSalesOrderLine> Lines { get; init; } = new();
}

public sealed class LocalSalesOrderLine
{
    public string Sku { get; init; } = string.Empty;
    public string? ItemName { get; init; }
    public double Quantity { get; init; }
    public string? Unit { get; init; }
    public decimal? Price { get; init; }
}

// Model for DebtorDeliveryNote information
public sealed class DebtorDeliveryNoteInfo
{
    public string? DebtorAccount { get; init; }
    public int? NoteNumber { get; init; }
    public string? NoteName { get; init; }
    public string? FileName { get; init; }
    public byte[]? FileData { get; init; }
    public string? MimeType { get; init; }
    public DateTime? Created { get; init; }
    public string? CreatedBy { get; init; }
}


