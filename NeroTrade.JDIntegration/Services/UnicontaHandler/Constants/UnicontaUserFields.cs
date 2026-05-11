namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Constants;

/// <summary>
/// Names of Uniconta user-defined fields used by the JD integration.
/// Centralised here so the (currently mixed-language, mixed-prefix) field names
/// live in one place and are referenced with compile-time safety.
/// The string values must match the user fields configured in Uniconta exactly.
/// </summary>
public static class UnicontaUserFields
{
    // Transfer flags — set by users in Uniconta to opt an entity in to syncing.
    public const string DebtorTransferFlag = "Xoverfort";
    public const string ItemTransferFlag = "XoverforVare";
    public const string SalesOrderTransferFlag = "Xoverfor1";
    public const string PurchaseOrderTransferFlag = "xTransferToJD";

    // State flags — set by the integration after a successful push.
    public const string CreatedAtJd = "xCreatedAtJD";
    public const string ReceivedByJd = "xReceivedByJD";

    // Sales order fields.
    public const string DeliveryDate = "xUdleveringsdato";
    public const string TrackingNote = "xSporingsnote";
    public const string DeliveryNoteText = "xNoteflgseddel";
    public const string Remark = "xBemaerk";
    public const string DeliveryType = "xDeliveryType";
    public const string TransportType = "xTransporttype";
    public const string DeliveryTime = "xtidspunkt";
    public const string CarrierMessage = "xbesked";

    // Item references.
    public const string ExternalSku = "xExternalSku";
}
