using Xunit;

namespace NeroTrade.UnicontaPlugin.Tests;

/// <summary>
/// Tripwire for the duplicated contract: the field names and enum value lists are
/// duplicated from NeroTrade.JDIntegration/Services/UnicontaHandler/Constants/UnicontaUserFields.cs
/// (no project reference allowed). If any of these fail, the plugin and the integration
/// have drifted apart and orders will slip past validation or fail in JD.
/// </summary>
public class PluginFieldNamesTests
{
    [Fact]
    public void FieldNames_MatchTheUnicontaUserFieldDefinitions()
    {
        Assert.Equal("xTransferToJD", PluginFieldNames.TransferToJd);
        Assert.Equal("xTrackingNote", PluginFieldNames.TrackingNote);
        Assert.Equal("xTransportTypes", PluginFieldNames.TransportType);
        Assert.Equal("xDeliveryType", PluginFieldNames.DeliveryType);
    }

    [Fact]
    public void TransportTypeValues_AreInTheConfiguredIndexOrder()
    {
        Assert.Equal(
            new[] { "JD Logistik Transport", "Ekstern Transport", "Afhenter Selv" },
            TransportTypeValues.InIndexOrder);
    }

    [Fact]
    public void DeliveryTypeValues_AreInTheConfiguredIndexOrder()
    {
        Assert.Equal(new[] { "GLS", "Palle Fragt" }, DeliveryTypeValues.InIndexOrder);
    }
}
