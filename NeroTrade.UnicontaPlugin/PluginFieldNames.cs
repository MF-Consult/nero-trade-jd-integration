namespace NeroTrade.UnicontaPlugin;

/// <summary>
/// User-field names on the Uniconta sales order (DebtorOrder) that the JD integration reads.
/// IMPORTANT: duplicated by design — these MUST match
/// NeroTrade.JDIntegration/Services/UnicontaHandler/Constants/UnicontaUserFields.cs.
/// The plugin has no project reference to the integration so the DLL can be built
/// and versioned on its own; PluginFieldNamesTests pins these values.
/// </summary>
public static class PluginFieldNames
{
    public const string TransferToJd = "xTransferToJD";
    public const string TrackingNote = "xTrackingNote";
    public const string TransportType = "xTransportTypes";
    public const string DeliveryType = "xDeliveryType";
}

/// <summary>
/// Values of the xTransportTypes enum user field, in the index order configured in Uniconta.
/// </summary>
public static class TransportTypeValues
{
    public const string JdLogistik = "JD Logistik Transport"; // index 0
    public const string Ekstern = "Ekstern Transport";        // index 1
    public const string AfhenterSelv = "Afhenter Selv";       // index 2

    public static readonly string[] InIndexOrder = { JdLogistik, Ekstern, AfhenterSelv };
}

/// <summary>
/// Values of the xDeliveryType enum user field, in the index order configured in Uniconta.
/// </summary>
public static class DeliveryTypeValues
{
    public const string Gls = "GLS";                // index 0
    public const string PalleFragt = "Palle Fragt"; // index 1

    public static readonly string[] InIndexOrder = { Gls, PalleFragt };
}
