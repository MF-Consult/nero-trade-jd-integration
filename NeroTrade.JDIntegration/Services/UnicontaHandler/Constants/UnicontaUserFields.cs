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
    public const string SalesOrderTransferFlag = "xTransferToJD";
    public const string PurchaseOrderTransferFlag = "xTransferToJD";

    // Process-status field on the purchase order (a value-list user field).
    public const string PurchaseOrderJdStatus = "xJDStatus";

    // Purchase order fields mapped onto the JD incoming shipment.
    public const string PurchaseOrderCarrier = "xCarrier";
    public const string PurchaseOrderRemark = "xRemarksForJD";

    // Free-text field where the integration writes the reason an order could not be pushed to JD.
    public const string IntegrationIssue = "xIntegrationIssue";

    // Sales order fields.
    public const string TrackingNote = "xTrackingNote";
    public const string DeliveryNoteText = "xTrackingNoteOnLabel";
    public const string Remark = "xRemarksForJD";
    public const string DeliveryType = "xDeliveryType";
    public const string TransportType = "xTransportTypes";
    public const string DeliveryTime = "xTimeForDelivery";
    public const string CarrierMessage = "xMessageForTransport";

    // Item references.
    public const string ExternalSku = "xExternalSku";
}

/// <summary>
/// Values for the purchase order <see cref="UnicontaUserFields.PurchaseOrderJdStatus"/> field.
/// Must match the value list configured on the field in Uniconta.
/// </summary>
public static class PurchaseOrderJdStatusValues
{
    public const string Created = "Oprettet";
    public const string ManualHandling = "Manuel handling";
    public const string Completed = "Færdigbehandlet";

    /// <summary>True when the integration should (re)process the order: never sent, or parked for manual handling.</summary>
    public static bool IsPending(string? status)
        => string.IsNullOrWhiteSpace(status)
           || string.Equals(status, ManualHandling, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The built-in <c>Group</c> value a sales order receives once its request order has been created in JD.
/// It acts as the "already sent" lock; <c>SyncRequestOrderStatusToUniconta</c> later replaces it with the
/// live JD status. Must exist in the order group value list in Uniconta.
/// </summary>
public static class SalesOrderJdGroup
{
    public const string Created = "Oprettet";
    public const string Failed = "Fejlet";
}
