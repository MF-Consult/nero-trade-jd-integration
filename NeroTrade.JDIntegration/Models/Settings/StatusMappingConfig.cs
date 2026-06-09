namespace NeroTrade.JDIntegration.Models.Settings;

// Mapping from JD Logistik enum values (verified against GET /api/statics/enums) to Uniconta
// debtor-order Group strings. All target strings MUST exist as ordregrupper in Uniconta:
//   Afsendt, Afventer, Afvist, Annulleret, Fejlet, Fremfundet, Godkendt, Oprettet, Pakket,
//   Planlagt, Udleveret.
//
// Do not target "Oprettet" or "Fejlet" here — those are reserved by SyncSalesOrdersToJd /
// ReadAllSalesOrdersAsync as the "already pushed to JD" / "push failed, retry on flueben"
// lock. Overwriting them from this map would either re-PDF a sent order or break retry.
public class StatusMappingConfig
{
    // JD EInOutRequestStatus: 0=Pending, 1=Approved, 2=Denied, 3=Cancelled
    public Dictionary<int, string> JdStatusToUnicontaGroup { get; set; } = new()
    {
        { 0, "Afventer" },   // Pending
        { 1, "Godkendt" },   // Approved
        { 2, "Afvist" },     // Denied
        { 3, "Annulleret" }  // Cancelled
    };

    // JD ERequestOrderStage: 0=Pending, 1=Denied, 2=Planned, 3=PendingDispatch, 4=Dispatched.
    // Stage 90 (Closed) is not returned by /api/statics/enums but still appears on live orders
    // — see JdRequestOrderStage.Closed and its use in the deletion-threshold guard.
    // Stage 0 (Pending) is intentionally omitted: SyncRequestOrderStatusToUniconta falls back
    // to status when stage <= Pending so a fresh Approved/Pending order lands on "Godkendt"
    // instead of regressing to "Afventer".
    public Dictionary<int, string> JdStageToUnicontaGroup { get; set; } = new()
    {
        { 1, "Afvist" },     // Denied
        { 2, "Planlagt" },   // Planned
        { 3, "Pakket" },     // PendingDispatch — picked & packed, awaiting carrier pickup
        { 4, "Afsendt" },    // Dispatched
        { 90, "Udleveret" }  // Closed — handed out / completed at JD
    };
}
