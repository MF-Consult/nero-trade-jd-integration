namespace NeroTrade.JDIntegration.Models.ExternalIntegration;

public class JdCatalogBarcode
{
    public long? id { get; set; }
    public string? barcode { get; set; }
}

public class JdCatalogItem
{
    public long? id { get; set; }
    public string? sku { get; set; }
    public string? name { get; set; }
    public string? description { get; set; }
    public bool metaEco { get; set; }
    public string? producedInCountryCode { get; set; }
    public string? enDescription { get; set; }
    public string? commodityCode { get; set; }
    public decimal? unitPrice { get; set; }
    public int? unitWeightInGrams { get; set; }
    public List<JdCatalogBarcode>? barcodes { get; set; }
}