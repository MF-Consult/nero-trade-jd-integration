namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;

using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;

public static class CountryHelper
{
    private static readonly Dictionary<string, (string Code, string Name)> CountryMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        // Nordic countries
        ["DENMARK"] = ("DK", "Denmark"),
        ["DANMARK"] = ("DK", "Denmark"),
        ["SWEDEN"] = ("SE", "Sweden"),
        ["SVERIGE"] = ("SE", "Sweden"),
        ["NORWAY"] = ("NO", "Norway"),
        ["NORGE"] = ("NO", "Norway"),
        ["FINLAND"] = ("FI", "Finland"),
        ["SUOMI"] = ("FI", "Finland"),
        ["ICELAND"] = ("IS", "Iceland"),
        ["ÍSLAND"] = ("IS", "Iceland"),

        // European countries
        ["GERMANY"] = ("DE", "Germany"),
        ["DEUTSCHLAND"] = ("DE", "Germany"),
        ["FRANCE"] = ("FR", "France"),
        ["NETHERLANDS"] = ("NL", "Netherlands"),
        ["NEDERLAND"] = ("NL", "Netherlands"),
        ["BELGIUM"] = ("BE", "Belgium"),
        ["BELGIË"] = ("BE", "Belgium"),
        ["BELGIQUE"] = ("BE", "Belgium"),
        ["AUSTRIA"] = ("AT", "Austria"),
        ["ÖSTERREICH"] = ("AT", "Austria"),
        ["SWITZERLAND"] = ("CH", "Switzerland"),
        ["SCHWEIZ"] = ("CH", "Switzerland"),
        ["SUISSE"] = ("CH", "Switzerland"),
        ["ITALY"] = ("IT", "Italy"),
        ["ITALIA"] = ("IT", "Italy"),
        ["SPAIN"] = ("ES", "Spain"),
        ["ESPAÑA"] = ("ES", "Spain"),
        ["PORTUGAL"] = ("PT", "Portugal"),
        ["POLAND"] = ("PL", "Poland"),
        ["POLSKA"] = ("PL", "Poland"),
        ["UNITED KINGDOM"] = ("GB", "United Kingdom"),
        ["GREAT BRITAIN"] = ("GB", "United Kingdom"),
        ["UK"] = ("GB", "United Kingdom"),
        ["IRELAND"] = ("IE", "Ireland"),
        ["ÉIRE"] = ("IE", "Ireland"),

        // Other common countries
        ["UNITED STATES"] = ("US", "United States"),
        ["USA"] = ("US", "United States"),
        ["CANADA"] = ("CA", "Canada"),
        ["AUSTRALIA"] = ("AU", "Australia"),
        ["NEW ZEALAND"] = ("NZ", "New Zealand"),
        ["JAPAN"] = ("JP", "Japan"),
        ["CHINA"] = ("CN", "China"),
        ["INDIA"] = ("IN", "India"),
        ["BRAZIL"] = ("BR", "Brazil"),
        ["BRAZIL"] = ("BR", "Brazil"),
        ["SOUTH AFRICA"] = ("ZA", "South Africa"),
        ["RUSSIA"] = ("RU", "Russia"),
        ["РОССИЯ"] = ("RU", "Russia"),
    };

    public static (string? Code, string? Name) GetCountryInfo(string? countryName)
    {
        if (string.IsNullOrWhiteSpace(countryName))
        {
            return (null, null);
        }

        if (CountryMappings.TryGetValue(countryName.Trim(), out var info))
        {
            return info;
        }

        // If it's already a country code, try to find the name
        var reverseLookup = CountryMappings.FirstOrDefault(kvp =>
            kvp.Value.Code.Equals(countryName.Trim(), StringComparison.OrdinalIgnoreCase));

        if (!reverseLookup.Equals(default(KeyValuePair<string, (string Code, string Name)>)))
        {
            return (countryName.Trim().ToUpper(), reverseLookup.Value.Name);
        }

        // Default fallback
        return (countryName.Trim().ToUpper(), countryName.Trim());
    }
}

public sealed class SalesOrderMapper
{
    public JdRequestOrderCreate Map(LocalSalesOrder so, IEnumerable<JdRequestOrderFileRef>? files = null)
    {
        var addressLine = string.Join(" ", new[] { so.DeliveryAddress1, so.DeliveryAddress2, so.DeliveryAddress3 }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        // Handle Shipmondo configuration
        JdRequestOrderShipmondo? shipmondo = null;
        DateTime? finalDeliveryDate = so.DeliveryDate;

        if (!string.IsNullOrWhiteSpace(so.DeliveryType))
        {
            var (carrierCode, productCode) = so.DeliveryType.ToUpperInvariant() switch
            {
                "GLS" => ("gls", "GLSDK_BP"),
                "PALLE FRAGT" => ("glimoe", "GLIMOE_PARCEL"),
                _ => (null, null)
            };

            if (carrierCode != null)
            {
                var productServices = new List<string>();
                if (so.DeliveryTime.HasValue)
                {
                    productServices.Add("TIMED_DELIVERY");
                    // Combine date and time
                    finalDeliveryDate = so.DeliveryDate?.Date.Add(so.DeliveryTime.Value.TimeOfDay);
                }

                shipmondo = new JdRequestOrderShipmondo
                {
                    carrierCode = carrierCode,
                    productCode = productCode,
                    productServices = productServices,
                    pickupPointId = null,
                    carrierInstructions = so.CarrierMessage,
                    draftShipmentId = null
                };
            }
        }
        else if (so.TransportType == "Ekstern Transport")
        {
            var productServices = new List<string>();
            if (so.DeliveryTime.HasValue)
            {
                productServices.Add("TIMED_DELIVERY");
                // Combine date and time
                finalDeliveryDate = so.DeliveryDate?.Date.Add(so.DeliveryTime.Value.TimeOfDay);
            }

            shipmondo = new JdRequestOrderShipmondo
            {
                carrierCode = "glimoe",
                productCode = "GLIMOE_PARCEL",
                productServices = productServices,
                pickupPointId = null,
                carrierInstructions = so.CarrierMessage,
                draftShipmentId = null
            };
        }

        // Get country information
        var (countryCode, countryName) = CountryHelper.GetCountryInfo(so.DeliveryCountryCode);

        var create = new JdRequestOrderCreate
        {
            date = finalDeliveryDate,
            text = $"SO {so.OrderNumber} - {so.RemarkText}",
            trackingNote = so.TrackingNote,
            deliveryNoteText = so.DeliveryNoteText,
            disableApprovalEmail = false,
            shipmondo = shipmondo,
            address = new JdAddress
            {
                name = so.DeliveryName,
                att = so.DebtorAccount,
                street = string.IsNullOrWhiteSpace(addressLine) ? null : addressLine,
                zip = so.DeliveryZip,
                city = so.DeliveryCity,
                countryCode = countryCode ?? "DK",
                country = countryName
            },
            contactPerson = new JdRequestOrderContactPerson
            {
                name = so.DeliveryName,
                title = null,
                department = null,
                company = null,
                vat = null,
                email = so.DeliveryContactEmail,
                telephoneDirect = null,
                telephoneMobile = so.DeliveryContactPhone
            },
            files = files?.ToList() ?? new List<JdRequestOrderFileRef>()
        };

        foreach (var line in so.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Sku)) continue;
            create.productItems.Add(new JdRequestOrderProductItem
            {
                quantity = (int)Math.Round(line.Quantity),
                catalog = new JdRequestOrderProductCatalog { sku = line.Sku }
            });
        }

        return create;
    }
}


