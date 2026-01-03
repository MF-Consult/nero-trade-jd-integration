namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;

using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;

public sealed class ItemMapper
{
    public JdCatalogItem Map(LocalInventoryItem item)
    {
        // Get country information using the helper
        var (countryCode, _) = CountryHelper.GetCountryInfo(item.ProducedInCountryCode);

        return new JdCatalogItem
        {
            sku = item.Sku?.Trim(),
            name = item.Name?.Trim(),
            description = item.Description?.Trim(),
            enDescription = item.EnDescription?.Trim(),
            commodityCode = item.CommodityCode?.Trim(),
            producedInCountryCode = countryCode ?? "DK",
            unitPrice = item.UnitPrice,
            unitWeightInGrams = item.UnitWeightInGrams,
            barcodes = (item.Barcodes ?? new()).Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => new JdCatalogBarcode { barcode = b.Trim() }).ToList(),
            metaEco = false
        };
    }
}


