namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;

using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;

public sealed class DebtorMapper
{
    public JdAddress Map(LocalDebtor debtor)
    {
        var street = string.Join(" ", new[] { debtor.Address1, debtor.Address2 }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

        // Get country information using the helper
        var (countryCode, countryName) = CountryHelper.GetCountryInfo(
            string.IsNullOrWhiteSpace(debtor.CountryCode) ? debtor.Country : debtor.CountryCode);

        return new JdAddress
        {
            name = debtor.Name?.Trim(),
            att = debtor.DebtorAccount?.Trim(),
            street = string.IsNullOrWhiteSpace(street) ? null : street,
            zip = debtor.ZipCode?.Trim(),
            city = debtor.City?.Trim(),
            country = countryName ?? (string.IsNullOrWhiteSpace(debtor.Country) ? "Denmark" : debtor.Country!.Trim()),
            countryCode = countryCode ?? "DK"
        };
    }
}


