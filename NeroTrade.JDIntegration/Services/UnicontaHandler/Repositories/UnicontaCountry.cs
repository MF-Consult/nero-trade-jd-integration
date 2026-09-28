namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Repositories;

using Uniconta.Common;

/// <summary>
/// Translates Uniconta's <see cref="CountryCode"/> enum to the ISO 3166-1 alpha-2 code JD requires.
///
/// <c>CountryCode.ToString()</c> is the enum <i>name</i> ("Denmark", "Türkiye", "UnitedKingdom"), not a code.
/// It used to be handed to <c>CountryHelper</c>, whose small hand-written table only knew ~30 countries and
/// passed every other name through as the "code" — so an item made in Türkiye reached JD as
/// <c>producedInCountryCode = "TÜRKIYE"</c> and was rejected (JD_VALIDATION_REJECTED, 2026-09-28).
///
/// The SDK ships <see cref="CountryISOCode"/> with the same ordinals as <see cref="CountryCode"/>
/// (verified against Uniconta.NetStandardAPI2.1 94.0.0.8: all 248 defined countries map to a two-letter
/// member, except Guadeloupe whose member is spelled "GLP").
/// </summary>
public static class UnicontaCountry
{
    public static string? ToIsoCode(CountryCode? country)
    {
        if (country is null || country == CountryCode.Unknown)
        {
            return null;
        }

        if (country == CountryCode.Guadeloupe)
        {
            return "GP";
        }

        var ordinal = (int)country.Value;
        if (!Enum.IsDefined(typeof(CountryISOCode), ordinal))
        {
            return null;
        }

        var iso = ((CountryISOCode)ordinal).ToString();
        return iso.Length == 2 ? iso.ToUpperInvariant() : null;
    }
}
