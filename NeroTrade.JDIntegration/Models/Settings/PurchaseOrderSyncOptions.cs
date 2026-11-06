namespace NeroTrade.JDIntegration.Models.Settings;

/// <summary>
/// Behaviour switches for <c>SyncPurchaseOrdersToJd</c>. Bound from the <c>PurchaseOrderSync</c> config
/// section (env: <c>PurchaseOrderSync__AutoCreateMissingItems</c>).
/// </summary>
public sealed class PurchaseOrderSyncOptions
{
    public const string SectionName = "PurchaseOrderSync";

    /// <summary>
    /// When a purchase order contains SKUs that are not in JD's catalog, create those catalog items from the
    /// Uniconta item master first (regardless of the item's <c>XoverforVare</c> flag) and then send the order,
    /// instead of rejecting the whole order as "No matching JD catalog item".
    ///
    /// Off by default: it makes the integration create master data in JD that nobody asked for explicitly, so it
    /// is switched on only once the customer has agreed to it. Items that do not exist in Uniconta, or that JD
    /// refuses, still leave the order rejected exactly as before.
    /// </summary>
    public bool AutoCreateMissingItems { get; init; }
}
