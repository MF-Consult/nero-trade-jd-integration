namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;

using System.Globalization;
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

        string? carrierCode = null;
        string? productCode = null;

        // "Afhenter Selv" = customer self-pickup: never assign a carrier, even if a delivery type
        // still lingers on the order. We deliberately check this BEFORE DeliveryType, because the
        // delivery-type switch below would otherwise book freight for a self-pickup order. (The
        // order is still sent to JD for picking — JD must be configured so it does not auto-assign
        // freight for a self-pickup order; confirm with JD if freight still appears.)
        var isSelfPickup = string.Equals(so.TransportType?.Trim(), "Afhenter Selv", StringComparison.OrdinalIgnoreCase);

        if (!isSelfPickup)
        {
            if (!string.IsNullOrWhiteSpace(so.DeliveryType))
            {
                (carrierCode, productCode) = so.DeliveryType.ToUpperInvariant() switch
                {
                    "GLS" => ("gls", "GLSDK_BP"),
                    "PALLE FRAGT" => ("glimoe", "GLIMOE_PARCEL"),
                    _ => (null, null)
                };
            }
            else if (so.TransportType == "Ekstern Transport")
            {
                (carrierCode, productCode) = ("glimoe", "GLIMOE_PARCEL");
            }
        }

        // JD rule (Mikkel, 2026-05-15): DK postal codes > 4999 must not ship with Glimø; reroute to
        // Esbjerg Gods Sjælland ("Pallefragt - Fyn/Jylland/Bornholm", zip range 5000-9999 + 3700-3799).
        if (carrierCode == "glimoe"
            && productCode == "GLIMOE_PARCEL"
            && IsDanishZipAboveGlimoeThreshold(so.DeliveryZip, so.DeliveryCountryCode))
        {
            carrierCode = "esbjerg_gods_sjaelland";
            productCode = "EGS_STDPL";
        }

        if (carrierCode != null && productCode != null)
        {
            var productServices = new List<string>();
            // PL_EXCHANGE is now opt-in per order via xByttepaller (so.ExchangePallets). It used to be
            // sent unconditionally on every pallet order, which made JD credit pallets that were never
            // returned. The product allow-list stays as a safety net so the code is never sent to a
            // product that does not support it.
            if (so.ExchangePallets && ShipmondoProductCatalog.SupportsService(productCode, ShipmondoServiceCodes.PalletExchange))
            {
                productServices.Add(ShipmondoServiceCodes.PalletExchange);
            }

            if (so.DeliveryTime.HasValue && ShipmondoProductCatalog.SupportsService(productCode, ShipmondoServiceCodes.TimedDelivery))
            {
                productServices.Add(ShipmondoServiceCodes.TimedDelivery);
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

        // Get country information
        var (countryCode, countryName) = CountryHelper.GetCountryInfo(so.DeliveryCountryCode);

        // JD request-order field mapping — each Uniconta note lands in its own JD field:
        //   text             → "Intern Note": the xRemarksForJD remark ("Bemærkning til JD") ONLY.
        //                      The "SO {n}" machine key is NO LONGER written here — it moved to
        //                      trackingNote (see below). Blank remark → null.
        //   trackingNote     → xTrackingNote (Sporingsnote) with the "SO {n}" key appended:
        //                      "{Sporingsnote} / SO {n}", or just "SO {n}" when the Sporingsnote is
        //                      blank. This is the field JdOrderHelper now parses the key back out of for
        //                      dedup/status sync. JD caps trackingNote at 30 chars (it is the shipping
        //                      label), so the Sporingsnote is trimmed as needed to guarantee the key is
        //                      never truncated — a silent truncation would break matching.
        //   deliveryNoteText → "Note på følgeseddel" (xTrackingNoteOnLabel) — label text only.
        var remark = so.RemarkText?.Trim();
        var internalNote = string.IsNullOrWhiteSpace(remark) ? null : remark;

        var trackingNote = BuildTrackingNoteWithSoKey(so.TrackingNote, so.OrderNumber);

        var deliveryNoteText = string.IsNullOrWhiteSpace(so.DeliveryNoteText)
            ? null
            : so.DeliveryNoteText.Trim();

        // JD contact person comes from the order's delivery-contact fields, not the delivery (location)
        // name. Previously contactPerson.name was set to DeliveryName, so JD showed the location name as
        // the contact. Each field is normalised to null when blank so it is only sent when filled in.
        var contactPersonName = string.IsNullOrWhiteSpace(so.DeliveryContactPerson) ? null : so.DeliveryContactPerson.Trim();
        var contactEmail = string.IsNullOrWhiteSpace(so.DeliveryContactEmail) ? null : so.DeliveryContactEmail.Trim();
        var contactPhone = string.IsNullOrWhiteSpace(so.DeliveryContactPhone) ? null : so.DeliveryContactPhone.Trim();

        var create = new JdRequestOrderCreate
        {
            date = finalDeliveryDate,
            text = internalNote,
            SourceOrderNumber = so.OrderNumber,
            trackingNote = trackingNote,
            deliveryNoteText = deliveryNoteText,
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
                name = contactPersonName,
                title = null,
                department = null,
                company = null,
                vat = null,
                email = contactEmail,
                telephoneDirect = contactPhone,
                telephoneMobile = contactPhone
            },
            files = files?.ToList() ?? new List<JdRequestOrderFileRef>()
        };

        foreach (var line in so.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Sku)) continue;
            // Skip service items (ItemType 1) - these are included in the PDF but should not be sent to JD
            if (line.ItemType == 1) continue;

            create.productItems.Add(new JdRequestOrderProductItem
            {
                quantity = (int)Math.Round(line.Quantity),
                catalog = new JdRequestOrderProductCatalog { sku = line.Sku }
            });
        }

        return create;
    }

    // JD caps trackingNote (the shipping label) at 30 chars. The "SO {n}" key MUST survive within that
    // budget because JdOrderHelper parses it back out of trackingNote for dedup/status sync — a silent
    // truncation of the key would break matching and re-send the order as "new".
    private const int JdTrackingNoteMaxLength = 30;

    /// <summary>
    /// Appends the "SO {orderNumber}" machine key to the Sporingsnote as "{Sporingsnote} / SO {n}"
    /// (or just "SO {n}" when the Sporingsnote is blank), trimming the Sporingsnote as needed so the
    /// whole string fits JD's 30-char trackingNote limit while the key is always kept whole.
    /// </summary>
    internal static string BuildTrackingNoteWithSoKey(string? sporingsnote, int orderNumber)
    {
        var soKey = $"SO {orderNumber}";
        var existing = sporingsnote?.Trim();

        if (string.IsNullOrWhiteSpace(existing))
        {
            // Order numbers are far under the 30-char budget, so the bare key always fits.
            return soKey;
        }

        const string separator = " / ";
        var budgetForExisting = JdTrackingNoteMaxLength - separator.Length - soKey.Length;

        if (budgetForExisting <= 0)
        {
            // Degenerate (implausibly long order number): the key wins the whole field.
            return soKey;
        }

        if (existing!.Length > budgetForExisting)
        {
            existing = existing[..budgetForExisting].TrimEnd();
        }

        return $"{existing}{separator}{soKey}";
    }

    private static bool IsDanishZipAboveGlimoeThreshold(string? deliveryZip, string? countryCode)
    {
        // Uniconta's DeliveryCountryCode is often the full name ("Denmark"/"Danmark"), not the ISO code.
        // Route through CountryHelper so "Denmark", "Danmark", "DK", and empty all normalize to "DK".
        var (normalizedCode, _) = CountryHelper.GetCountryInfo(countryCode);
        var isDk = string.IsNullOrWhiteSpace(countryCode)
            || string.Equals(normalizedCode, "DK", StringComparison.OrdinalIgnoreCase);
        if (!isDk) return false;

        return int.TryParse(deliveryZip?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var zip)
               && zip > 4999;
    }
}


