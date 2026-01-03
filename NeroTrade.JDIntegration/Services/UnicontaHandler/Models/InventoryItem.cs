namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Models;

public sealed class LocalInventoryItem
{
    public string Sku { get; init; } = string.Empty; // Item Number
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? EnDescription { get; init; }
    public string? CommodityCode { get; init; }
    public decimal? UnitPrice { get; init; }
    public int? UnitWeightInGrams { get; init; }
    public string? ProducedInCountryCode { get; init; }
    public List<string>? Barcodes { get; init; }
}


